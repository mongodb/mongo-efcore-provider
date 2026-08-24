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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits an expression tree translating various types of binding expressions.
/// </summary>
internal sealed partial class MongoProjectionBindingExpressionVisitor : ExpressionVisitor
{
    private readonly Dictionary<ProjectionMember, Expression> _projectionMapping = new();
    private readonly Stack<ProjectionMember> _projectionMembers = new();
    private readonly Dictionary<ParameterExpression, CollectionShaperExpression> _collectionShaperMapping = new();
    private readonly Stack<INavigation> _includedNavigations = new();

    private MongoQueryExpression _queryExpression;

    // The top-level expression handed to THIS Translate() call — i.e. the (post-shaper-replace)
    // selector body as a whole. Used by the bare filtered-count rebuild arm below to distinguish the BARE
    // selector-body spelling (Select(b => b.Posts.Count(pred)), where the Count call itself IS this root) from
    // the SAME Count call reached as one leaf of a WRAPPED anonymous/DTO projection that separately declined to
    // Fallback (a correlated/non-renderable/primitive-collection/differently-shaped element predicate) — see
    // that arm's own comment for why the distinction is load-bearing.
    private Expression _translatedRootExpression;

    /// <summary>
    /// Perform translation of the <paramref name="expression" /> that belongs to the
    /// supplied <paramref name="queryExpression"/>.
    /// </summary>
    /// <param name="queryExpression">The <see cref="MongoQueryExpression"/> the expression being translated belongs to.</param>
    /// <param name="expression">The <see cref="Expression"/> being translated.</param>
    /// <returns>The translated expression tree.</returns>
    public Expression Translate(
        MongoQueryExpression queryExpression,
        Expression expression)
    {
        _queryExpression = queryExpression;
        _projectionMembers.Push(new ProjectionMember());
        _translatedRootExpression = expression;

        var result = Visit(expression);

        _queryExpression.ReplaceProjectionMapping(_projectionMapping);
        _projectionMapping.Clear();
        _queryExpression = null;
        _translatedRootExpression = null;

        _projectionMembers.Clear();

        return MatchTypes(result, expression.Type);
    }

    /// <inheritdoc />
    public override Expression Visit(Expression expression)
    {
        switch (expression)
        {
            case null:
                return null;

            case NewExpression:
            case MemberInitExpression:
            case StructuralTypeShaperExpression:
            case MaterializeCollectionNavigationExpression:
                return base.Visit(expression);

#if EF8 || EF9
            case ParameterExpression parameterExpression:
                if (_collectionShaperMapping.ContainsKey(parameterExpression))
                {
                    return parameterExpression;
                }
                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                    == true)
                {
                    return Expression.Call(
                        GetParameterValueMethodInfo.MakeGenericMethod(parameterExpression.Type),
                        QueryCompilationContext.QueryContextParameter,
                        Expression.Constant(parameterExpression.Name));
                }

                throw new InvalidOperationException(CoreStrings.TranslationFailed(parameterExpression.Print()));
#else
            case QueryParameterExpression queryParameter:
                return Expression.Call(
                    GetParameterValueMethodInfo.MakeGenericMethod(queryParameter.Type),
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(queryParameter.Name));

            case ParameterExpression parameterExpression:
                return _collectionShaperMapping.ContainsKey(parameterExpression)
                    ? parameterExpression
                    : throw new InvalidOperationException(CoreStrings.TranslationFailed(parameterExpression.Print()));
#endif

            case ConstantExpression:
                return expression;

            // Already resolved by index against OUR OWN query expression, AND the query is still natively
            // routed (Route == Projection) — pass through unchanged rather than trying to re-derive a
            // projection mapping for it. This arises for a native SelectMany's projected element:
            // NativeSelectManyBinder/BuildSelectManyResultShaper build this shaper directly via
            // MongoQueryExpression.AddToProjection (mirroring the GroupBy/Distinct alias-flatten shaper),
            // embedded inside the trivial TransparentIdentifier(Outer, Inner) resultSelector EF's
            // nav-expansion always synthesizes for SelectMany. That wrapper is then unwrapped by a MANDATORY
            // subsequent .Select(ti => ti.Inner) which reaches THIS visitor a second time — folding to our
            // already-resolved shaper via ReplacingExpressionVisitor's NewExpression-member fold — so it must
            // be passed straight through rather than re-bound (the rest of this visitor assumes its input is
            // raw member accesses over shaper types it resolves itself, not an already-bound
            // ProjectionBindingExpression leaf). Mirrors the "already bound by index... (e.g., from join
            // rebinding)" precedent in VisitExtension's StructuralTypeShaperExpression case.
            // The Route == Projection guard is load-bearing, NOT redundant: it is what distinguishes this
            // case from a projected Select applied AFTER a GroupBy/Distinct (also built via AddToProjection-
            // by-index) — that shape's OWN post-terminal guard already called MarkNotNativelyRepresentable()
            // (flipping Route to Fallback) BEFORE this visitor ever runs, specifically so the shape is
            // detected as unsupported (see NativeGroupByTests.Select_after_GroupBy_is_unsupported_and_never_
            // returns_silent_null_data) rather than silently reading a since-invalidated by-index projection
            // through the driver-LINQ fallback path. Passing through unconditionally here would silently
            // defeat that guard; gating on Route == Projection keeps it intact while still letting the
            // still-native SelectMany case through.
            case ProjectionBindingExpression { Index: not null } projectionBindingExpression
                when projectionBindingExpression.QueryExpression == _queryExpression
                     && _queryExpression.Select.Route == NativeRoute.Projection:
                return projectionBindingExpression;

            case MemberExpression memberExpression:
                var currentProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[currentProjectionMember] = memberExpression;

                return new ProjectionBindingExpression(_queryExpression, currentProjectionMember, expression.Type);

            // Arithmetic computed projection leaf: register the whole binary node as ONE projection
            // leaf, exactly like a MemberExpression, so it maps to a single ProjectionMember slot. Without this,
            // the default walk would visit each operand's MemberExpression separately, both writing the SAME
            // current ProjectionMember and silently producing wrong data ((A*B)² instead of A*B). Gated to the
            // same arithmetic operators NativeProjectionBinder accepts.
            // The Route == Projection guard is load-bearing (mirrors the { Index: not null } case above): it is
            // what CONFINES this mapping to the native path. The binder only populates Select.Projection (flipping
            // Route to Projection) for a projection whose EVERY leaf is natively representable; a MIXED shape like
            // Select(c => new { c, Total = c.Age * c.Score }) has an entity leaf the binder cannot represent, so it
            // stays Route == Fallback and routes to the mixed shaper (MongoMixedProjectionBindingRemovingExpression-
            // Visitor). Without this guard the case would still fire on that fallback shape and hand the mixed
            // shaper a raw BinaryExpression it cannot read (TryResolveFieldAccess returns null for it), silently
            // reading a non-existent field literally named after the alias. Gating on Route == Projection makes the
            // case fire ONLY when the binder already accepted the whole projection — i.e. only on the native path —
            // and fall through to the default walk (pre-existing behavior) for every mixed/fallback shape.
            case BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                    or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo } binaryExpression
                when _queryExpression.Select.Route == NativeRoute.Projection:
                var arithProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[arithProjectionMember] = binaryExpression;
                return new ProjectionBindingExpression(_queryExpression, arithProjectionMember, expression.Type);

            // Native numeric-cast projection leaf: register the WHOLE
            // UnaryExpression{Convert} node as ONE projection member, exactly like the arithmetic case above.
            // Without it, the default visitor walk (base.Visit -> VisitUnary's
            // default recursion) visits only the OPERAND (the raw member access) and drops the Convert from
            // _projectionMapping entirely; the read side (MongoProjectionBindingRemovingExpressionVisitor) then
            // has no way to know a CONVERTED value ($toInt/$toLong/$toDouble/$toDecimal) was projected under
            // this alias and misreads it through the PRE-CAST property's own serializer — e.g. re-interpreting
            // the $toInt output as the source double property's raw representation. See that visitor's
            // ProjectionBindingExpression case for the read-side half of this fix, and its own comment for why
            // its pre-existing type-mismatch guard would otherwise convert this into a translate-time crash in
            // EVERY query mode (not merely a silent misread) once this leaf is admitted.
            // The Route == Projection guard is load-bearing for the same reason it is on the arithmetic case:
            // NativeProjectionBinder sets Route = Projection only when EVERY leaf -- including this one -- is
            // natively representable (i.e. translates to a MongoConvertExpression, never a bare unwrapped
            // value); a mixed/fallback shape must fall through to the ordinary default walk untouched.
            // The `Operand is not StructuralTypeShaperExpression` exclusion keeps this case disjoint from the
            // UNRELATED structural navigation-Convert shape VisitMember's own switch matches on, at
            // VisitMember's `case UnaryExpression unaryExpression: shaperExpression = unaryExpression.Operand as
            // StructuralTypeShaperExpression; ...` (this file, VisitMember) -- a SINGLE-level
            // `Convert(structuralTypeShaperExpression, T)`, produced when navigating into an embedded/entity
            // sub-member. That shape's operand is a shaper/entity-projection node, never a plain
            // member/constant/parameter/arithmetic operand a numeric cast leaf can be built from.
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                    Operand: not StructuralTypeShaperExpression } castExpression
                when _queryExpression.Select.Route == NativeRoute.Projection:
                var castProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[castProjectionMember] = castExpression;
                return new ProjectionBindingExpression(_queryExpression, castProjectionMember, expression.Type);

            case MethodCallExpression methodCallExpression
                when IsScalarMethodPropertyAccess(methodCallExpression):
                var projMember = GetCurrentProjectionMember();
                _projectionMapping[projMember] = methodCallExpression;

                return new ProjectionBindingExpression(_queryExpression, projMember, expression.Type);

            // A computed-arithmetic leaf (e.g. c.Age * c.Score) mixed into a projection alongside a whole
            // entity reference (which forces the client-side "mixed projection" shaper — see
            // MongoMixedProjectionBindingRemovingExpressionVisitor). Register the whole binary expression as
            // a single projection-mapping leaf here, without visiting into its operands: the default walk
            // (via base.Visit below) would visit Left and Right independently, each writing the SAME
            // ProjectionMember dictionary slot (the current one hasn't changed), so the second operand would
            // silently clobber the first (e.g. Age * Score would materialise as Score * Score).
            // Scoped to operands that are themselves simple scalar reads / nested arithmetic over those
            // (IsSimpleArithmeticLeaf) — NOT method calls such as a collection-navigation Sum()/Count(),
            // which must still decompose through the normal walk so their own (more specific) translation
            // failures / cross-collection guards continue to fire as before.
            case BinaryExpression binaryExpression
                when IsArithmeticNodeType(binaryExpression.NodeType) && IsSimpleArithmeticLeaf(binaryExpression):
                var arithmeticMember = GetCurrentProjectionMember();
                _projectionMapping[arithmeticMember] = binaryExpression;

                return new ProjectionBindingExpression(_queryExpression, arithmeticMember, expression.Type);

            default:
                return base.Visit(expression);
        }
    }

    private static bool IsArithmeticNodeType(ExpressionType nodeType)
        => nodeType is ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
            or ExpressionType.Divide or ExpressionType.Modulo;

    private static bool IsSimpleArithmeticLeaf(Expression expression)
    {
        expression = expression.RemoveConvert();

        return expression switch
        {
            ConstantExpression => true,
            MemberExpression => true,
            MethodCallExpression methodCallExpression when methodCallExpression.TryGetEFPropertyArguments(out _, out _) => true,
            BinaryExpression binaryExpression when IsArithmeticNodeType(binaryExpression.NodeType) =>
                IsSimpleArithmeticLeaf(binaryExpression.Left) && IsSimpleArithmeticLeaf(binaryExpression.Right),
            _ => false,
        };
    }

    /// <inheritdoc />
    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case StructuralTypeShaperExpression structuralTypeShaperExpression:
                {
                    var projectionBindingExpression =
                        (ProjectionBindingExpression)structuralTypeShaperExpression.ValueBufferExpression;

                    EntityProjectionExpression entityProjection;
                    if (projectionBindingExpression.Index is int existingIndex
                        && projectionBindingExpression.QueryExpression == _queryExpression)
                    {
                        // Already bound by index to our query expression (e.g., from join rebinding)
                        entityProjection = (EntityProjectionExpression)_queryExpression.Projection[existingIndex].Expression;
                    }
                    else
                    {
                        entityProjection = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
                            projectionBindingExpression.ProjectionMember);
                    }

                    return structuralTypeShaperExpression.Update(
                        new ProjectionBindingExpression(
                            _queryExpression, _queryExpression.AddToProjection(entityProjection), typeof(ValueBuffer)));
                }

            case MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression:
                if (TryBindNativeArrayProjection(materializeCollectionNavigationExpression, out var arrayShaper))
                {
                    return arrayShaper;
                }

                if (materializeCollectionNavigationExpression.Navigation is INavigation embeddableNavigation
                    && embeddableNavigation.IsEmbedded())
                {
                    var visited = base.Visit(materializeCollectionNavigationExpression.Subquery);

                    // If the element type has its own embedded navigation, the Select arm above rebuilds this
                    // as an IEnumerable<T>-typed Enumerable.Select rather than the navigation's declared List<T>,
                    // which fails Expression.New's member-type check. Convert is a no-op at runtime:
                    // MongoProjectionBindingRemovingExpressionVisitor discards this shape later for the
                    // correctly-typed CollectionShaperExpression.
                    return visited != null && visited.Type != materializeCollectionNavigationExpression.Type
                        ? Expression.Convert(visited, materializeCollectionNavigationExpression.Type)
                        : visited;
                }

                return base.VisitExtension(materializeCollectionNavigationExpression);

            case IncludeExpression includeExpression:
                {
                    if (includeExpression.Navigation is not INavigation includableNavigation)
                    {
                        throw new InvalidOperationException(
                            $"Including navigation '{
                                nameof(includeExpression.Navigation)
                            }' is not supported.");
                    }

                    if (!includableNavigation.IsEmbedded() && includableNavigation.IsCollection)
                    {
                        var lookup = new LookupExpression(includableNavigation);

                        // For multi-level Include where the declaring entity is a cross-collection
                        // reference (handled by LeftJoin producing _outer/_inner), the $lookup
                        // localField must be prefixed to reference the inner sub-document.
                        // When a LeftJoin restructures the document (_outer/_inner),
                        // $lookup fields must be prefixed with the correct sub-document path.
                        if (_queryExpression.UsesDriverJoinFields)
                        {
                            var declaringType = includableNavigation.DeclaringEntityType;
                            var rootType = _queryExpression.CollectionExpression.EntityType;
                            if (declaringType == rootType || declaringType.IsOwned())
                            {
                                lookup.LocalField = $"_outer.{lookup.LocalField}";
                                lookup.As = $"_outer.{lookup.As}";
                            }
                            else
                            {
                                lookup.LocalField = $"_inner.{lookup.LocalField}";
                                lookup.As = $"_inner.{lookup.As}";
                            }
                        }
                        else
                        {
                            // Flat multi-lookup mode: when two or more cross-collection reference
                            // navigations were chained (e.g. OrderDetail.Order.Customer.Orders), the
                            // reference chain is emitted as a series of root-level $lookup+$unwind
                            // stages aliased "_lookup_<Nav>" rather than the driver's _outer/_inner
                            // shape. A trailing collection Include whose declaring entity is one of
                            // those unwound intermediates must match against that intermediate's
                            // sub-document, so its $lookup localField needs the "_lookup_<Nav>." prefix.
                            // The output "as" is nested under the same intermediate sub-document because
                            // the shaper reads the collection array relative to the intermediate's
                            // ParentAccessExpression (i.e. "_lookup_<Nav>._lookup_<Collection>").
                            var declaringType = includableNavigation.DeclaringEntityType;
                            var intermediateMatches = _queryExpression.GetPendingLookups().Where(
                                l => l.IsReference
                                     && l.ForceUnwind
                                     && l.TargetEntityType == declaringType).ToList();

                            // The intermediate is matched by its target entity type, not by its alias. When
                            // more than one reference lookup targets the same entity type — e.g. two reference
                            // navigations to the same type, or a self-referential chain — the match is
                            // ambiguous: there is no basis here to tell which intermediate sub-document this
                            // collection Include is nested under, and choosing arbitrarily would prefix the
                            // $lookup with the wrong "_lookup_<Nav>." path and silently return wrong results.
                            // Fail translation cleanly instead.
                            if (intermediateMatches.Count > 1)
                            {
                                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                            }

                            var intermediateLookup = intermediateMatches.Count == 1 ? intermediateMatches[0] : null;
                            if (intermediateLookup != null)
                            {
                                lookup.LocalField = $"{intermediateLookup.As}.{lookup.LocalField}";
                                lookup.As = $"{intermediateLookup.As}.{lookup.As}";
                            }
                        }

                        // Extract filtered Include pipeline stages (OrderBy, Skip, Take)
                        // and nested ThenInclude $lookups from the NavigationExpression.
                        ExtractNestedIncludePipeline(includeExpression.NavigationExpression, lookup, includableNavigation.TargetEntityType);
                        _queryExpression.AddLookup(lookup);
                        return RewriteCollectionIncludeForLookup(includeExpression, includableNavigation);
                    }

                    _includedNavigations.Push(includableNavigation);
                    var newIncludeExpression = base.VisitExtension(includeExpression);
                    _includedNavigations.Pop();
                    return newIncludeExpression;
                }
            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
        }
    }

    /// <inheritdoc />
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        // A projected cross-collection collection navigation (e.g. select new { ..., Orders = c.Orders.ToList() }).
        // EF Core lowers this to Enumerable.ToList(Queryable.Select(Queryable.Where(DbSet<Target>(), joinPred), selector)).
        // There is no enclosing IncludeExpression to set up the $lookup, so bind it here to a CollectionShaperExpression
        // that reads from a dedicated "_lookup_<Nav>" array, mirroring the cross-collection Include path.
        if (TryBindProjectedCollectionNavigation(methodCallExpression, out var boundCollection))
        {
            return boundCollection;
        }

        // A projected cross-collection collection-navigation Count (e.g. select new { ..., c.Orders.Count }).
        // EF Core lowers this to Queryable.Count(Queryable.Where(DbSet<Target>(), joinPred)) with no enclosing
        // IncludeExpression. Register a "_lookup_<Nav>" $lookup (injected right after the root source) and bind
        // the count as a scalar projection; the EF-to-driver translator rewrites the subtree into a server-side
        // { $size: "$_lookup_<Nav>" }.
        if (TryBindProjectedCollectionNavigationCount(methodCallExpression, out var boundCount))
        {
            return boundCount;
        }

        // An OWNED (embedded) collection-navigation count leaf in a native projection —
        // `select new { ..., N = b.Posts.Count }`, and (since IsCanonicalCount admits the predicated overloads
        // too) the FILTERED spelling `select new { ..., N = b.Posts.Count(pred) }`. Register the whole
        // Count/LongCount call (predicate-less or predicated) as ONE projection member, exactly like the
        // arithmetic case in Visit above.
        //
        // Why this block is load-bearing: without it a NATIVE-route count would be rebuilt by the Queryable
        // switch below into a CLIENT-SIDE Enumerable.Count fold, over a shaper that reads `Posts` from a
        // document the native $project has already reduced to {Title, N} — the array is not there to count.
        //
        // Position: this must run BEFORE the generic fall-through's methodCallExpression.Update(...) and
        // before the Queryable switch's own Visit(Arguments[0]) below, so the count is never rebuilt for
        // client-side counting. The switch's own Count/LongCount arm never runs for a NATIVE-route count:
        // this block returns first, unconditionally, whenever both Route == Projection and
        // IsCanonicalCount(Method) hold — matching is by reference equality against eight specific,
        // fixed-arity canonical MethodInfo definitions, so arity needs no separate conjunct (a call of some
        // other arity cannot spuriously equal a definition it isn't). So the switch's arm only ever sees a
        // NON-PROJECTION-route shape (Route != Projection) — the two arms are disjoint by construction.
        //
        // This must come AFTER TryBindProjectedCollectionNavigationCount above. The actual protection for the
        // reference-collection $lookup + $size shape is NativeProjectionBinder's own pendingLookups list (the
        // `pendingLookups.Add(lookup)` in TryTranslateProjectedCollectionCount, drained at the end of
        // TryPopulateNativeProjection, plus MongoQueryExpression.Lookup.cs's alias-based dedup inside
        // AddLookup) — the $lookup this branch's projection-member registration would additionally trigger is
        // redundant with that when Route == Projection, and this branch is guarded off entirely when Route !=
        // Projection. The ordering is kept anyway as cheap defence-in-depth (it also avoids the switch's
        // Visit(Arguments[0]) side effects on this call).
        //
        // The Route == Projection guard is load-bearing for the same reason it is on the arithmetic case:
        // NativeProjectionBinder sets Route = Projection only when EVERY leaf is natively representable, so a
        // mixed or fallback shape must fall through untouched.
        //
        // Matching is by canonical MethodInfo, not by name: this block RETURNS UNCONDITIONALLY once it
        // matches, so a false positive here would silently hijack an unrelated projection member rather than
        // merely miss an optimization — and this area's own pitfall list requires reference equality against
        // the canonical constants (see Query/AGENTS.md, "Reference-equality on MethodInfo"). Generic methods
        // must be compared as definitions: an open definition and a constructed instantiation are never
        // reference-equal.
        if (_queryExpression.Select.Route == NativeRoute.Projection
            && IsCanonicalCount(methodCallExpression.Method))
        {
            var countProjectionMember = GetCurrentProjectionMember();
            _projectionMapping[countProjectionMember] = methodCallExpression;
            return new ProjectionBindingExpression(_queryExpression, countProjectionMember, methodCallExpression.Type);
        }

        if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var memberName))
        {
            var visitedSource = Visit(source);

            StructuralTypeShaperExpression shaperExpression;
            switch (visitedSource)
            {
                case StructuralTypeShaperExpression shaper:
                    shaperExpression = shaper;
                    break;

                case UnaryExpression unaryExpression:
                    shaperExpression = unaryExpression.Operand as StructuralTypeShaperExpression;
                    if (shaperExpression == null || unaryExpression.NodeType != ExpressionType.Convert)
                    {
                        return null;
                    }

                    break;

                case ParameterExpression parameterExpression:
                    if (!_collectionShaperMapping.TryGetValue(parameterExpression, out var collectionShaper))
                    {
                        return null;
                    }

                    shaperExpression = (StructuralTypeShaperExpression)collectionShaper.InnerShaper;
                    break;

                default:
                    return null;
            }

            EntityProjectionExpression innerEntityProjection;
            switch (shaperExpression.ValueBufferExpression)
            {
                case ProjectionBindingExpression innerProjectionBindingExpression:
                    innerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[
                        innerProjectionBindingExpression.Index.Value].Expression;
                    break;

                case UnaryExpression unaryExpression:
                    innerEntityProjection = (EntityProjectionExpression)((UnaryExpression)unaryExpression.Operand).Operand;
                    break;

                default:
                    throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
            }

            Expression navigationProjection;
            var navigation = _includedNavigations.FirstOrDefault(n => n.Name == memberName);
            if (navigation == null)
            {
                navigationProjection = innerEntityProjection.BindMember(memberName, visitedSource.Type, out var propertyBase);
                if (propertyBase is not INavigation projectedNavigation
                    || (!projectedNavigation.IsEmbedded() && !_includedNavigations.Contains(projectedNavigation)))
                {
                    return null;
                }

                navigation = projectedNavigation;
            }
            else
            {
                navigationProjection = innerEntityProjection.BindNavigation(navigation);
            }

            switch (navigationProjection)
            {
                case EntityProjectionExpression entityProjection:
                    return new StructuralTypeShaperExpression(
                        navigation.TargetEntityType,
                        Expression.Convert(Expression.Convert(entityProjection, typeof(object)), typeof(ValueBuffer)),
                        nullable: true);

                case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                    {
                        var innerShaperExpression = new StructuralTypeShaperExpression(
                            navigation.TargetEntityType,
                            Expression.Convert(
                                Expression.Convert(objectArrayProjectionExpression.InnerProjection, typeof(object)),
                                typeof(ValueBuffer)),
                            nullable: true);

                        return new CollectionShaperExpression(
                            objectArrayProjectionExpression,
                            innerShaperExpression,
                            navigation,
                            innerShaperExpression.StructuralType.ClrType);
                    }

                default:
                    throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
            }
        }

        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(Queryable))
        {
            var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
            var visitedSource = Visit(methodCallExpression.Arguments[0]);

            switch (method.Name)
            {
                case nameof(Queryable.AsQueryable)
                    when genericMethod == QueryableMethods.AsQueryable:
                    // Unwrap AsQueryable
                    return visitedSource;

                case nameof(Queryable.Select)
                    when genericMethod == QueryableMethods.Select:
                    if (visitedSource is not CollectionShaperExpression shaper)
                    {
                        return null;
                    }

                    var lambda = methodCallExpression.Arguments[1].UnwrapLambdaFromQuote();

                    _collectionShaperMapping.Add(lambda.Parameters.Single(), shaper);

                    lambda = Expression.Lambda(Visit(lambda.Body), lambda.Parameters);
                    return Expression.Call(
                        EnumerableMethods.Select.MakeGenericMethod(method.GetGenericArguments()),
                        shaper,
                        lambda);

                // Count/LongCount over a materialized collection shaper. EF hands us
                // Queryable.Count(IQueryable<T>), but the visited source is a CollectionShaperExpression whose
                // Type is the navigation's CLR type (List<T>). MatchTypes (see below) returns that expression
                // UNTOUCHED for this target — it does not attempt a Convert at all, because
                // targetType.TryGetItemType() is non-null for an IQueryable<T> parameter (it exposes
                // IEnumerable<T>) — so the List<T>-typed shaper is passed straight through as the argument, and
                // the generic fall-through's methodCallExpression.Update(...) throws ArgumentException because
                // Expression.Call's own BCL argument validation requires the argument type to be ASSIGNABLE to
                // the parameter type, which List<T> is not to IQueryable<T>. The underlying gap is that this
                // Queryable overload is never rebuilt against its Enumerable equivalent, the way the Select
                // case above already does for the same source shape; rebuilding it here counts the materialized
                // collection instead. Since this fold runs before MongoQueryMode is read, that crash fired in
                // Native, DriverLinq and NativeOnly alike.
                //
                // Deliberately narrow. First/Any/Sum/... are stranded on the same fall-through for the same
                // reason (never rebuilt against their Enumerable equivalents); adding a case per method changes
                // type coercion on a path every projection walks, in all three modes, so it is left as a
                // follow-on, together with the non-bare filtered-count arm further down this same switch — one
                // root cause, one file, but the two halves are separable. The REBUILD branch below can only
                // fire on a shape that throws today.
                //
                // The DECLINE branch (visitedSource is not a CollectionShaperExpression) is intentionally
                // `break`, not `return null`: falling through to the untouched generic fall-through below
                // reproduces EXACTLY the behaviour this input had before this case existed, which is what makes
                // the arm purely additive — `return null` here would instead fold through
                // MatchTypes(null, typeof(int)) -> Expression.Default(int), silently returning 0 for a
                // bare-scalar count projection body, for any input that takes this branch.
                //
                // `break` causes the generic fall-through to Visit(Arguments[0]) a SECOND time (the first Visit,
                // at the top of this `if (method.DeclaringType == typeof(Queryable))` block, computed
                // `visitedSource`). That second Visit is NOT universally side-effect-free: an interposed
                // `Distinct` one level up (which has no switch case of its own and so falls through this exact
                // same way) demonstrates a genuine duplicate-registration crash —
                // `Select(b => new { N = b.Posts.Select(p => p.Heading).Distinct().Count() })` throws
                // `ArgumentException: An item with the same key has already been added. Key: o` from
                // `_collectionShaperMapping.Add` in the adjacent Select case, reached via a second Visit of the
                // SAME Distinct-call subtree. So `break`'s safety here rests on THIS input never reaching that
                // `.Add` on a second pass, not on the fall-through being side-effect-free in general.
                // That interposed-operator family (Distinct/Take/Reverse/DefaultIfEmpty/Concat between an
                // owned-collection Select and a terminal operator) hard-fails at translation in EVERY mode and
                // is tracked as a follow-on.
                //
                // No LINQ shape has been found that reaches THIS case's decline branch with a
                // non-CollectionShaperExpression source — the closest candidate,
                // `Select(b => new { N = b.Posts.Select(p => p.Heading).Count() })`, does not reach it either,
                // because EF Core's own query compiler fuses `Select(f).Count()` into `Count()` upstream (a
                // Count has no dependency on a preceding Select's projection), so the source Visit(Arguments[0])
                // sees is the SAME CollectionShaperExpression a bare `b.Posts.Count()` would produce, taking the
                // REBUILD branch instead. That is a measured fact about this one shape, not a general guarantee
                // about every shape that might reach this case in the future.
                //
                // Unreachable for a NATIVE count projection: that is claimed earlier in VisitMethodCall by the
                // Route == Projection registration, which pushes the count into $project instead.
                case nameof(Queryable.Count)
                    when genericMethod == QueryableMethods.CountWithoutPredicate:
                case nameof(Queryable.LongCount)
                    when genericMethod == QueryableMethods.LongCountWithoutPredicate:
                    if (visitedSource is not CollectionShaperExpression countShaper)
                    {
                        break;
                    }

                    return Expression.Call(
                        (method.Name == nameof(Queryable.Count)
                            ? EnumerableMethods.CountWithoutPredicate
                            : EnumerableMethods.LongCountWithoutPredicate)
                        .MakeGenericMethod(method.GetGenericArguments()),
                        countShaper);

                // The same rebuild as the arm immediately above, for the PREDICATED Count/LongCount
                // overloads — this delivers the BARE filtered-count projection,
                // `Select(b => b.Posts.Count(p => ...))`, and only that shape. A NATIVE filtered-count
                // projection never reaches here: the Route == NativeRoute.Projection registration earlier in
                // VisitMethodCall (gated by IsCanonicalCount, which admits both arities) already claims the
                // predicated overloads and pushes the count into $project instead — so this arm only ever
                // sees a shape that is NOT going native.
                //
                // "Bare spelling" here means a bare selector BODY, not merely a bare TOP node — narrower than
                // the arm above it. `Select(b => b.Posts.Count * 2)` folds client-side (the unfiltered arm has
                // no `_translatedRootExpression` identity check), but the filtered analogue,
                // `Select(b => b.Posts.Count(p => ...) * 2)`, still hard-fails in every mode: the Count call
                // is an OPERAND of the top-level `*`, not the selector body itself, so identity fails and this
                // arm declines. Widening to "the Count call appears anywhere reachable from the root, with no
                // interposed shaper reference" is a follow-on. Note the asymmetry: the UNFILTERED
                // `Select(b => b.Posts.Count * 2)` is a graceful decline with correct values in the two
                // fallback modes, while this FILTERED spelling is a hard fail in all three, and the WRAPPED
                // filtered form is native. The `new {...}` is the difference, not the arithmetic.
                //
                // The Enumerable overload takes a Func<,>, not an Expression<Func<,>>, so the predicate lambda
                // must be UNQUOTED — UnwrapLambdaFromQuote (used the same way by the adjacent Select case above)
                // handles the Queryable spelling's Quote and passes an already-bare lambda through unchanged.
                //
                // The predicate lambda is deliberately NOT re-Visited (contrast the adjacent Select case, which
                // DOES visit its lambda body): the rebuilt Enumerable.Count runs CLIENT-SIDE over MATERIALIZED
                // Post elements, so the predicate must stay ordinary CLR code operating on a real Post instance.
                // Visiting it would rewrite its member accesses into shaper reads against a document the fold no
                // longer has — there is no BsonDocument here, only the List<Post> the countShaper already
                // materialized.
                //
                // The DECLINE branch is `break`, never `return null`, for the identical reason the arm above's
                // comment gives: `return null` would fold through MatchTypes(null, typeof(int)) ->
                // Expression.Default(int) and silently return 0 for a bare-scalar filtered-count projection body.
                //
                // A CAPTURED LOCAL declines here too: EF Core parameterizes a captured local into an EF
                // query-parameter node (a typed `QueryParameterExpression` on EF10, a specially-named
                // `ParameterExpression` on EF8/EF9 — see NativeQueryParameter.TryGetQueryParameterName), and
                // since the predicate lambda above is NOT re-Visited that node survives into the rebuilt
                // `Enumerable.Count` call unresolved. Compiling it as ordinary CLR code then throws
                // `ArgumentException: must be reducible node` from `Expression.ReduceAndCheck()` deep in the
                // LambdaCompiler — a worse failure than a clean decline. `ContainsQueryParameter` declines the
                // whole leaf before that call is built, so this spelling keeps failing with the SAME
                // InvalidOperationException("could not be translated") every other declined shape in this file
                // fails with, rather than trading it for a confusing `ArgumentException`.
                //
                // MUST BE THE BARE SELECTOR BODY ITSELF, not a leaf nested inside a WRAPPED anonymous/DTO
                // projection. A WRAPPED projection's element predicate can decline to Fallback for reasons
                // unrelated to this arm (correlated-beyond-element, a non-renderable predicate like
                // `StartsWith`, a primitive-element collection, or the structurally distinct `Where(pred).Count()`
                // shape) — see NativeOwnedCollectionFilteredCountTests' pinned `..._still_hard_fail(s)_in_every_mode`
                // tests. Those shapes reach this SAME switch arm too (Route == Fallback for a DIFFERENT reason
                // than a bare selector body), and `visitedSource` is STILL a genuine CollectionShaperExpression
                // for them (visiting a real owned-collection navigation produces one regardless of Route) — so
                // the shaper-type check alone does not distinguish a bare spelling from an unrelated decline
                // residual. Reference-equality against `_translatedRootExpression` (the top-level expression
                // this Translate() call started with — see its own doc comment) does: it is true only when
                // this Count call IS the entire selector body. For a WRAPPED shape the Count call is nested
                // inside a NewExpression/MemberInit, so identity fails and this arm declines as before.
                //
                // NOT REDUNDANT WITH `ContainsShaperReference` BELOW — the two guards protect DIFFERENT
                // residual shapes. The identity guard alone does NOT restore the WRAPPED CORRELATED residual
                // (`Correlated_primitive_and_where_count_filtered_projections_still_hard_fail_in_every_mode`'s
                // first row) — a proxy for "does this predicate reference the enclosing shaper", not that
                // property itself; see `ContainsShaperReference`'s own doc comment for why a BARE correlated
                // predicate slips past identity but is caught by the structural check. Conversely,
                // `ContainsShaperReference` alone does NOT restore the WRAPPED NON-RENDERABLE residual
                // (`Non_renderable_element_predicate_filtered_projection_still_hard_fails_in_every_mode`, the
                // `StartsWith` case): that predicate references only its own element parameter `p` — no shaper
                // node, no query parameter — so nothing about it is structurally distinguishable from the bare
                // spelling except that the Count call is nested inside a `new {...}` rather than being the whole
                // selector body. Only the identity check catches THAT one. Both guards stay.
                case nameof(Queryable.Count)
                    when genericMethod == QueryableMethods.CountWithPredicate:
                case nameof(Queryable.LongCount)
                    when genericMethod == QueryableMethods.LongCountWithPredicate:
                    if (visitedSource is not CollectionShaperExpression filteredCountShaper
                        || !ReferenceEquals(methodCallExpression, _translatedRootExpression))
                    {
                        break;
                    }

                    var filteredCountLambda = methodCallExpression.Arguments[1].UnwrapLambdaFromQuote();
                    if (ContainsQueryParameter(filteredCountLambda.Body) || ContainsShaperReference(filteredCountLambda.Body))
                    {
                        break;
                    }

                    return Expression.Call(
                        (method.Name == nameof(Queryable.Count)
                            ? EnumerableMethods.CountWithPredicate
                            : EnumerableMethods.LongCountWithPredicate)
                        .MakeGenericMethod(method.GetGenericArguments()),
                        filteredCountShaper,
                        filteredCountLambda);
            }
        }

        var newObject = Visit(methodCallExpression.Object);
        var newArguments = new Expression[methodCallExpression.Arguments.Count];
        for (var i = 0; i < newArguments.Length; i++)
        {
            var argument = methodCallExpression.Arguments[i];
            var newArgument = Visit(argument);
            newArguments[i] = MatchTypes(newArgument, argument.Type);
        }

        Expression updatedMethodCallExpression = methodCallExpression.Update(
            newObject != null ? MatchTypes(newObject, methodCallExpression.Object?.Type) : null,
            newArguments);

        if (newObject?.Type.IsNullableType() == true && !methodCallExpression.Object.Type.IsNullableType())
        {
            var nullableReturnType = methodCallExpression.Type.MakeNullable();
            if (!methodCallExpression.Type.IsNullableType())
            {
                updatedMethodCallExpression = Expression.Convert(updatedMethodCallExpression, nullableReturnType);
            }

            return Expression.Condition(
                Expression.Equal(newObject, Expression.Default(newObject.Type)),
                Expression.Constant(null, nullableReturnType),
                updatedMethodCallExpression);
        }

        return updatedMethodCallExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitNew(NewExpression newExpression)
    {
        if (newExpression.Arguments.Count == 0) return newExpression;
        var hasMembers = newExpression.Members != null;

        var newArguments = new Expression[newExpression.Arguments.Count];
        for (var i = 0; i < newArguments.Length; i++)
        {
            var argument = newExpression.Arguments[i];

            if (hasMembers)
            {
                EnterProjectionMember(newExpression.Members[i]);
            }

            var visitedArgument = Visit(argument);

            if (hasMembers)
            {
                ExitProjectionMember();
            }

            if (visitedArgument == null)
            {
                return null!;
            }

            newArguments[i] = MatchTypes(visitedArgument, argument.Type);
        }

        return newExpression.Update(newArguments);
    }

    protected override MemberAssignment VisitMemberAssignment(MemberAssignment memberAssignment)
    {
        EnterProjectionMember(memberAssignment.Member);
        var visitedExpression = Visit(memberAssignment.Expression);
        ExitProjectionMember();

        if (visitedExpression == null)
        {
            return null!;
        }

        return memberAssignment.Update(MatchTypes(visitedExpression, memberAssignment.Expression.Type));
    }

    /// <inheritdoc />
    protected override Expression VisitMemberInit(MemberInitExpression memberInitExpression)
    {
        var newExpression = Visit(memberInitExpression.NewExpression);
        if (newExpression == null)
        {
            return null!;
        }

        var newBindings = new MemberBinding[memberInitExpression.Bindings.Count];
        for (var i = 0; i < newBindings.Length; i++)
        {
            if (memberInitExpression.Bindings[i].BindingType != MemberBindingType.Assignment)
            {
                return null!;
            }

            newBindings[i] = VisitMemberBinding(memberInitExpression.Bindings[i]);

            if (newBindings[i] == null)
            {
                return null!;
            }
        }

        return memberInitExpression.Update((NewExpression)newExpression, newBindings);
    }

    protected override Expression VisitMember(MemberExpression memberExpression)
    {
        var innerExpression = Visit(memberExpression.Expression);

        StructuralTypeShaperExpression shaperExpression;
        switch (innerExpression)
        {
            case StructuralTypeShaperExpression shaper:
                shaperExpression = shaper;
                break;

            case UnaryExpression unaryExpression:
                shaperExpression = unaryExpression.Operand as StructuralTypeShaperExpression;
                if (shaperExpression == null
                    || unaryExpression.NodeType != ExpressionType.Convert)
                {
                    return NullSafeUpdate(innerExpression);
                }

                break;

            default:
                return NullSafeUpdate(innerExpression);
        }

        EntityProjectionExpression innerEntityProjection;
        switch (shaperExpression.ValueBufferExpression)
        {
            case ProjectionBindingExpression innerProjectionBindingExpression:
                innerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[
                    innerProjectionBindingExpression.Index.Value].Expression;
                break;

            case UnaryExpression unaryExpression:
                // Unwrap EntityProjectionExpression when the root entity is not projected
                innerEntityProjection = (EntityProjectionExpression)((UnaryExpression)unaryExpression.Operand).Operand;
                break;

            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(memberExpression.Print()));
        }

        var navigationProjection = innerEntityProjection.BindMember(
            memberExpression.Member, innerExpression.Type, out var propertyBase);

        if (propertyBase is not INavigation navigation || !navigation.IsEmbedded())
        {
            return NullSafeUpdate(innerExpression);
        }

        switch (navigationProjection)
        {
            case EntityProjectionExpression entityProjection:
                return new StructuralTypeShaperExpression(
                    navigation.TargetEntityType,
                    Expression.Convert(Expression.Convert(entityProjection, typeof(object)), typeof(ValueBuffer)),
                    nullable: true);

            case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                {
                    var innerShaperExpression = new StructuralTypeShaperExpression(
                        navigation.TargetEntityType,
                        Expression.Convert(
                            Expression.Convert(objectArrayProjectionExpression.InnerProjection, typeof(object)),
                            typeof(ValueBuffer)),
                        nullable: true);

                    return new CollectionShaperExpression(
                        objectArrayProjectionExpression,
                        innerShaperExpression,
                        navigation,
                        innerShaperExpression.StructuralType.ClrType);
                }

            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(memberExpression.Print()));
        }

        Expression NullSafeUpdate(Expression expression)
        {
            Expression updatedMemberExpression = memberExpression.Update(
                expression != null ? MatchTypes(expression, memberExpression.Expression.Type) : expression);

            if (expression?.Type.IsNullableType() == true)
            {
                var nullableReturnType = memberExpression.Type.MakeNullable();
                if (!memberExpression.Type.IsNullableType())
                {
                    updatedMemberExpression = Expression.Convert(updatedMemberExpression, nullableReturnType);
                }

                updatedMemberExpression = Expression.Condition(
                    Expression.Equal(expression, Expression.Default(expression.Type)),
                    Expression.Constant(null, nullableReturnType),
                    updatedMemberExpression);
            }

            return updatedMemberExpression;
        }
    }


    /// <inheritdoc />
    protected override ElementInit VisitElementInit(ElementInit elementInit)
        => elementInit.Update(elementInit.Arguments.Select(e => MatchTypes(Visit(e), e.Type)));

    /// <inheritdoc />
    protected override Expression VisitNewArray(NewArrayExpression newArrayExpression)
        => newArrayExpression.Update(newArrayExpression.Expressions.Select(e => MatchTypes(Visit(e), e.Type)));

    /// <summary>
    /// Binds an owned entity-COLLECTION projection leaf
    /// (<c>Select(b =&gt; new { b.Title, b.Posts })</c>) on the fully-native projection route, where the array is
    /// read back from the <c>$project</c> OUTPUT ALIAS rather than from the navigation's own document path.
    /// Registers the array as ONE projection member and returns a <see cref="CollectionShaperExpression"/> over
    /// an <see cref="ArrayAliasProjectionExpression"/>; returns <see langword="false"/> for every other shape,
    /// which then binds exactly as it did before this slice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The alias is never carried on the node.</b> It is derived by the post-processor
    /// (<c>MongoQueryExpression.ApplyProjection</c>) from this <see cref="ProjectionMember"/> — the same
    /// mechanism every scalar leaf uses, and the same name <c>NativeProjectionBinder</c> derived from the same
    /// member on the emit side — so the emit-side and shaper-side alias spaces agree by construction.
    /// </para>
    /// <para>
    /// <b>Why this runs HERE, at the top of the <c>MaterializeCollectionNavigationExpression</c> visit, and not
    /// in <see cref="VisitMember"/>'s <c>navigationProjection</c> switch.</b> Reaching
    /// that switch requires first visiting the OWNER shaper (<c>Visit(memberExpression.Expression)</c>), whose
    /// <see cref="StructuralTypeShaperExpression"/> case calls <c>MongoQueryExpression.AddToProjection</c>. That
    /// leaves an entry in <c>Projection</c>, and <c>ApplyProjection</c> RETURNS EARLY when
    /// <c>Projection.Any()</c> — so no projection member is ever rewritten to its <c>Constant(index)</c> form
    /// and every sibling leaf's binding then dies in
    /// <c>MongoProjectionBindingRemovingExpressionVisitor.GetProjectionIndex</c>
    /// (<c>InvalidOperationException</c> from <c>GetConstantValue&lt;int&gt;</c>, at shaper-compile time, in
    /// every query mode). Registering BEFORE descending is exactly what the count leaf does at the top of
    /// <see cref="VisitMethodCall"/> and what the arithmetic leaf does in <see cref="Visit"/>; this is the same
    /// invariant, not a new one.
    /// </para>
    /// <para>
    /// <b>The <c>Route == Projection</c> guard is load-bearing</b>, for the same reason it is on the count and
    /// arithmetic cases: <c>NativeProjectionBinder</c> sets <c>Route = Projection</c> only when EVERY leaf is
    /// natively representable. A mixed or fallback shape still fetches whole documents, so its array must keep
    /// being read at the navigation's document path — i.e. it must fall through to the
    /// <c>ObjectArrayProjectionExpression</c> arm in <see cref="VisitMember"/> and be shaped client-side by the
    /// mixed shaper exactly as before this slice.
    /// </para>
    /// <para>
    /// <b>Admissibility is NOT decided here.</b> It is the shared
    /// <see cref="NativeTranslation.NativeProjectionBinder.IsNativeArrayProjectionLeaf"/>, called by the emit side
    /// too — the two sides MUST admit the same set, because the failure mode when they disagree is silent wrong
    /// data rather than a decline. That method's own remarks carry the full rationale, including why the shape is
    /// restricted to a root-declared navigation whose alias equals its document element name (the shaper built
    /// here is alias-addressed but may still be handed an UN-projected document by a late fallback, so the two
    /// reads have to coincide). Whatever narrows or widens this shape belongs there, in one place.
    /// </para>
    /// <para>
    /// The <see cref="RootReferenceExpression"/> is constructed fresh rather than lifted off the root
    /// <see cref="EntityProjectionExpression"/>; that is safe because <see cref="EntityTypedExpression"/>
    /// equality/hashing is by <see cref="IEntityType"/>, so it is interchangeable as a
    /// <c>_projectionBindings</c>/<c>_ownerMappings</c> key with the instance the query expression built.
    /// </para>
    /// </remarks>
    private bool TryBindNativeArrayProjection(
        MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression,
        out Expression arrayShaper)
    {
        arrayShaper = null;

        // The alias comes from the SAME ProjectionMember the post-processor will derive the $project alias from,
        // so this side and the emit side cannot disagree about it — including when the emit side registered an
        // alias OVERRIDE for that member, which is read here through the same single carrier
        // MongoQueryExpression.ApplyProjection reads. A bare selector body has no last member, so without the
        // override this derivation yields null and the alias-agreement conjunct below could never hold.
        var arrayProjectionMember = GetCurrentProjectionMember();
        var arrayMemberName = arrayProjectionMember.Last?.Name;
        var arrayAlias = _queryExpression.Select.TryGetProjectionAlias(arrayMemberName, out var overriddenAlias)
            ? overriddenAlias
            : arrayMemberName;

        if (_queryExpression.Select.Route != NativeRoute.Projection
            || !NativeProjectionBinder.IsNativeArrayProjectionLeaf(
                materializeCollectionNavigationExpression.Navigation as INavigation,
                _queryExpression.CollectionExpression.EntityType,
                arrayAlias))
        {
            return false;
        }

        var navigation = (INavigation)materializeCollectionNavigationExpression.Navigation!;
        var aliasedArray = new ArrayAliasProjectionExpression(
            navigation,
            new RootReferenceExpression(navigation.DeclaringEntityType));

        _projectionMapping[arrayProjectionMember] = aliasedArray;

        var innerShaper = new StructuralTypeShaperExpression(
            navigation.TargetEntityType,
            Expression.Convert(
                Expression.Convert(aliasedArray.InnerProjection, typeof(object)),
                typeof(ValueBuffer)),
            nullable: true);

        arrayShaper = new CollectionShaperExpression(
            new ProjectionBindingExpression(_queryExpression, arrayProjectionMember, aliasedArray.Type),
            innerShaper,
            navigation,
            innerShaper.StructuralType.ClrType);

        return true;
    }

    private ProjectionMember GetCurrentProjectionMember()
        => _projectionMembers.Peek();

    private void EnterProjectionMember(MemberInfo memberInfo)
        => _projectionMembers.Push(_projectionMembers.Peek().Append(memberInfo));

    private void ExitProjectionMember()
        => _projectionMembers.Pop();

    /// <summary>
    /// Checks whether <paramref name="method"/> is one of the eight canonical <c>Count</c>/<c>LongCount</c>
    /// methods — predicate-less AND predicated, the <see cref="Queryable"/> four from EF Core's
    /// <c>QueryableMethods</c> and the <see cref="Enumerable"/> four from this provider's own
    /// <c>EnumerableMethods</c> port — by reference equality on the generic method DEFINITION.
    /// </summary>
    /// <remarks>
    /// Reference equality, not name matching: see the comment at the call site in
    /// <c>VisitMethodCall</c> for why a false positive there is consequential. Comparing definitions
    /// rather than the passed-in <see cref="MethodInfo"/> is required because a constructed generic
    /// method is never reference-equal to its open definition. A non-generic method cannot be any of
    /// the eight, so it declines before <c>GetGenericMethodDefinition</c> is called (which would throw).
    /// </remarks>
    private static bool IsCanonicalCount(MethodInfo method)
    {
        if (!method.IsGenericMethod)
        {
            return false;
        }

        var definition = method.GetGenericMethodDefinition();

        return definition == QueryableMethods.CountWithoutPredicate
            || definition == QueryableMethods.LongCountWithoutPredicate
            || definition == QueryableMethods.CountWithPredicate
            || definition == QueryableMethods.LongCountWithPredicate
            || definition == EnumerableMethods.CountWithoutPredicate
            || definition == EnumerableMethods.LongCountWithoutPredicate
            || definition == EnumerableMethods.CountWithPredicate
            || definition == EnumerableMethods.LongCountWithPredicate;
    }

    /// <summary>
    /// Reports whether <paramref name="expression"/> contains an EF Core query-parameter node
    /// anywhere in its tree (see <see cref="NativeQueryParameter.TryGetQueryParameterName"/>), so a captured value
    /// in the BARE filtered-count projection's element predicate (e.g. <c>b.Posts.Count(p => p.Rank > threshold)</c>,
    /// <c>threshold</c> a captured local) can be declined BEFORE the predicate lambda is rebuilt against the
    /// client-side <see cref="EnumerableMethods.CountWithPredicate"/>/<see cref="EnumerableMethods.LongCountWithPredicate"/>
    /// call. That rebuild deliberately does not re-Visit the lambda body (see the call site's comment), so an
    /// EF query-parameter node reaching it would survive unresolved into ordinary CLR code and throw
    /// <c>ArgumentException: must be reducible node</c> when the lambda compiler tries to compile it — a worse,
    /// confusing failure than the clean decline this check produces instead.
    /// </summary>
    private static bool ContainsQueryParameter(Expression expression)
    {
        var detector = new QueryParameterDetector();
        detector.Visit(expression);
        return detector.Found;
    }

    /// <summary>
    /// Stops descending the moment an EF query-parameter node is found — this only needs to answer "is one
    /// present anywhere", not enumerate all of them.
    /// </summary>
    private sealed class QueryParameterDetector : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression Visit(Expression node)
        {
            if (Found || node is null)
            {
                return node;
            }

            if (NativeQueryParameter.TryGetQueryParameterName(node, out _))
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }
    }

    /// <summary>
    /// Reports whether <paramref name="expression"/> contains a provider/EF Core
    /// SHAPER node anywhere in its tree — <see cref="StructuralTypeShaperExpression"/>,
    /// <see cref="ProjectionBindingExpression"/>, or <see cref="EntityProjectionExpression"/>. This is the
    /// STRUCTURAL property the bare filtered-count rebuild arm actually needs to guard against, which the
    /// top-level-identity check (<c>ReferenceEquals(methodCallExpression, _translatedRootExpression)</c>) was
    /// only ever a PROXY for: that check protects the WRAPPED residual-decline shapes (the Count call is nested
    /// inside a NewExpression/MemberInit, so identity fails), but a BARE correlated predicate — e.g.
    /// <c>Select(b => b.Posts.Count(p => p.Title == b.Title))</c> — has this Count call AS its top-level
    /// selector body, so identity holds and the arm would otherwise proceed. By the time this visitor runs,
    /// <c>ReplacingExpressionVisitor</c> has already rewritten every occurrence of the outer <c>b</c> — INCLUDING
    /// the one inside the predicate lambda — to the query root's entity shaper (a <see cref="StructuralTypeShaperExpression"/>).
    /// Since the predicate is deliberately not re-Visited (see the call site's comment), that unresolved shaper
    /// reference would otherwise survive into the rebuilt client-side <see cref="EnumerableMethods.CountWithPredicate"/>
    /// call and crash downstream at shaper-compile time with a confusing <c>KeyNotFoundException</c>
    /// ("...'EmptyProjectionMember'...") instead of the clean, pre-existing <c>InvalidOperationException</c>
    /// ("could not be translated") every other declined shape in this file gets.
    /// </summary>
    private static bool ContainsShaperReference(Expression expression)
    {
        var detector = new ShaperReferenceDetector();
        detector.Visit(expression);
        return detector.Found;
    }

    /// <summary>
    /// Stops descending the moment a shaper node is found — this only needs to answer "is one present anywhere".
    /// </summary>
    private sealed class ShaperReferenceDetector : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression Visit(Expression node)
        {
            if (Found || node is null)
            {
                return node;
            }

            if (node is StructuralTypeShaperExpression or ProjectionBindingExpression or EntityProjectionExpression)
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }
    }

    /// <summary>
    /// Checks whether a method call expression represents a scalar property access that should
    /// be stored in the projection mapping (like <see cref="MemberExpression"/>), rather than
    /// being fully visited. This covers <c>EF.Property</c> (for non-navigation properties) and
    /// <c>Mql.Field</c> calls.
    /// </summary>
    private static bool IsScalarMethodPropertyAccess(MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var memberName))
        {
            // By the time this runs, the selector's own parameter has already been
            // replaced with a StructuralTypeShaperExpression wherever it appears in the tree — including
            // inside a Convert node the C# compiler inserts when EF.Property's `object entity` receiver is
            // an unconstrained generic type parameter rather than a directly-typed reference (e.g. inside a
            // generic helper like `ShadowPropertySelect<TIn, TOut>`). `RemoveConvert()` mirrors both the
            // Convert-aware switch a few lines below (case UnaryExpression) and IsSelectorParameter's own
            // `receiver.RemoveConvert()` call further down this file — without it, a Convert-wrapped shaper
            // failed this pattern match, so this method returned false, the call fell through to the generic
            // recursive walk instead of being registered as a projection leaf, and the query silently
            // returned the CLR default (null) instead of the shadow property's value. Confirmed live via
            // NorthwindMiscellaneousQueryMongoTest.Select_Property_when_shadow_unconstrained_generic_method,
            // which exercises exactly this Convert-wrapped receiver shape.
            //
            // This method has NO Route == NativeRoute.Projection guard (unlike the sibling arithmetic case
            // in Visit's switch above), and _projectionBindingExpressionVisitor.Translate is called
            // unconditionally for every Select — so this check, and this RemoveConvert(), run on the
            // fallback/mixed routes too, not only the native one. That is deliberate, not an oversight to
            // gate away: the read side this registration feeds (MongoProjectionBindingRemovingExpressionVisitor.
            // TryResolveFieldAccess / TryResolveFieldAccessSource) also calls RemoveConvert() unconditionally,
            // on every route. Adding a Route guard here would make the write side disagree with a read side
            // that already unwraps everywhere — the asymmetry a guard would introduce, not remove.
            if (source.RemoveConvert() is StructuralTypeShaperExpression { StructuralType: IEntityType entityType })
            {
                var navigation = entityType.FindNavigation(memberName);
                // Embedded navigations should be handled by VisitMethodCall
                return navigation == null || !navigation.IsEmbedded();
            }

            return false;
        }

        // Mql.Field<TDoc, TField>() is always a scalar field extraction
        if (methodCallExpression.Method is { Name: "Field", DeclaringType.FullName: "MongoDB.Driver.Mql" })
        {
            return true;
        }

        return false;
    }

    private static Expression MatchTypes(
        Expression expression,
        Type targetType)
        => expression == null
            ? Expression.Default(targetType)
            : targetType != expression.Type && targetType.TryGetItemType() == null
                ? Expression.Convert(expression, targetType)
                : expression;

    private static readonly MethodInfo GetParameterValueMethodInfo
        = typeof(MongoProjectionBindingExpressionVisitor)
            .GetTypeInfo().GetDeclaredMethod(nameof(GetParameterValue));

#if EF8 || EF9
    private static T GetParameterValue<T>(
        QueryContext queryContext,
        string parameterName)
        => (T)queryContext.ParameterValues[parameterName];
#else
    private static T GetParameterValue<T>(
        QueryContext queryContext,
        string parameterName)
        => (T)queryContext.Parameters[parameterName];
#endif
}
