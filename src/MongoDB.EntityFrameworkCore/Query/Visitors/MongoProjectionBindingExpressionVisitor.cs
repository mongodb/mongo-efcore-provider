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

            // A CONSTRUCTED sub-entity leaf (EF-447, `new { Book = new Book { Id = e.Id, ... } }`) that
            // NativeProjectionBinder already translated into a MongoDocumentConstructionExpression at emit
            // time. Register the WHOLE New/MemberInit node as ONE opaque projection member — exactly like the
            // arithmetic/cast/conditional leaves above it — rather than falling through to the default recursive
            // walk (the `case NewExpression: case MemberInitExpression:` arm immediately below, which every
            // OTHER shape, including the OUTER wrapping `new { Book = ..., Score = ... }` itself, still uses).
            // Recursing here would register "Book.Id"/"Book.Title" as their own nested ProjectionMembers mapped
            // to the RAW e.Id/e.Title member accesses — correct for driver-LINQ's whole-document read, but wrong
            // for the native alias-addressed read this leaf actually needs (its members live nested under the
            // "Book" $project alias, not at the document root).
            // TryGetNativeDocumentConstructionLeaf calls back into NativeProjectionBinder's own emit-side result
            // (via Select.Projection) rather than restating its own recognition predicate — the gate-must-call-
            // the-fix's-own-predicate discipline this codebase requires (see Query/AGENTS.md).
            case NewExpression or MemberInitExpression
                when TryGetNativeDocumentConstructionLeaf(expression, out var documentConstructionLeaf):
                {
                    var constructionMember = GetCurrentProjectionMember();
                    _projectionMapping[constructionMember] = documentConstructionLeaf;
                    return new ProjectionBindingExpression(_queryExpression, constructionMember, expression.Type);
                }

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

            // Native conditional projection leaf: register the WHOLE ConditionalExpression node as ONE
            // projection member, exactly like the arithmetic and cast cases above (NOT caught by the earlier
            // unconditional `case MemberExpression memberExpression:` -- a ConditionalExpression is not a
            // MemberExpression). Without this, the default recursive walk would visit Test/IfTrue/IfFalse
            // independently, writing the SAME ProjectionMember slot three times and silently producing wrong
            // data. The Route == Projection guard is load-bearing for the same reason as the arithmetic case's:
            // it confines this mapping to a projection NativeProjectionBinder already accepted in full, so a
            // mixed/fallback shape falls through to the ordinary default walk untouched.
            case ConditionalExpression when _queryExpression.Select.Route == NativeRoute.Projection:
                var conditionalMember = GetCurrentProjectionMember();
                _projectionMapping[conditionalMember] = expression;
                return new ProjectionBindingExpression(_queryExpression, conditionalMember, expression.Type);

            // A reference-collection-nav First/FirstOrDefault projection leaf (EF-449,
            // `a.IdentificationMethods.FirstOrDefault().Method`) that NativeProjectionBinder already recognized
            // and staged as a MongoCorrelatedReducerLeaf at emit time. Nav-expansion hoists the reduced member
            // access INSIDE the reducer (the tree arrives as `...Select(m => m.Method).FirstOrDefault()`, a
            // MethodCallExpression, not a bare MemberExpression), so without this case the default recursive walk
            // (base.Visit below) would try to translate the whole nav-expanded LINQ chain itself — including the
            // untranslatable `DbSet<IdentificationMethod>()` root — and throw
            // "The LINQ expression '...' could not be translated" for every leaf of this kind, accepted or not.
            // Register the WHOLE node as one opaque projection member instead, exactly like the arithmetic/cast/
            // conditional leaves above: the read side (MongoProjectionBindingRemovingExpressionVisitor) never
            // inspects this mapped expression's shape for this leaf kind (TryResolveFieldAccess falls through to
            // its default no-property case for an arbitrary MethodCallExpression), it only needs the ALIAS —
            // already staged onto Select.Projection as a MongoElementRefExpression over
            // "_lookup_<Nav>.<Member>" — so mapping the raw pre-nav-expansion-chain node here is sufficient.
            // TryGetCorrelatedReducerLeaf looks the answer up by alias against the emit side's own committed
            // result (CorrelatedReducerLeaves) rather than re-deriving admissibility here, mirroring
            // TryGetNativeDocumentConstructionLeaf's discipline.
            case MethodCallExpression correlatedReducerCandidate
                when TryGetCorrelatedReducerLeaf(correlatedReducerCandidate, out _):
                var reducerMember = GetCurrentProjectionMember();
                _projectionMapping[reducerMember] = correlatedReducerCandidate;

                return new ProjectionBindingExpression(_queryExpression, reducerMember, expression.Type);

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

    /// <summary>
    /// True when <paramref name="expression"/> is the exact <see cref="NewExpression"/>/<see cref="MemberInitExpression"/>
    /// that <c>NativeProjectionBinder.TryPopulateNativeProjection</c> already translated, for the CURRENT
    /// projection member, into a <see cref="MongoDocumentConstructionExpression"/> — i.e. this leaf is going
    /// native (EF-447). Looks the answer up in <c>Select.Projection</c> (the emit side's own committed result)
    /// rather than re-deriving admissibility here, so the bind side can never admit a shape the emit side
    /// declined — see <c>NativeTranslation.NativeProjectionBinder.TryGetDocumentConstructionLeaf</c> for the
    /// actual recognition predicate.
    /// </summary>
    /// <remarks>
    /// NOT a reference-equality check against <see cref="MongoDocumentConstructionExpression.OriginalExpression"/>
    /// — this visitor is reached with <paramref name="expression"/> already rewritten by
    /// <see cref="ReplacingExpressionVisitor"/> (the outer selector's own parameter substituted for the source
    /// shaper before this visitor ever runs), so a deep member access inside the construction (e.g. <c>c.Id</c>)
    /// makes every ancestor <c>NewExpression</c>/<c>MemberInitExpression</c>'s own <c>Update(...)</c> call return
    /// a NEW node — the tree NativeProjectionBinder translated at emit time is a different object by the time
    /// this runs. Matching on alias + CLR type is sufficient: a given projection member position names exactly
    /// one leaf for the whole query.
    /// </remarks>
    private bool TryGetNativeDocumentConstructionLeaf(
        Expression expression, out MongoDocumentConstructionExpression construction)
    {
        construction = null;

        if (_queryExpression.Select.Route != NativeRoute.Projection)
        {
            return false;
        }

        var memberName = GetCurrentProjectionMember().Last?.Name;
        if (memberName is null)
        {
            return false;
        }

        var alias = _queryExpression.Select.TryGetProjectionAlias(memberName, out var overriddenAlias)
            ? overriddenAlias
            : memberName;

        foreach (var projection in _queryExpression.Select.Projection)
        {
            if (projection.Alias == alias
                && projection.Expression is MongoDocumentConstructionExpression candidate
                && candidate.OriginalExpression.Type == expression.Type)
            {
                construction = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="expression"/> is the CURRENT projection member's own reference-collection-nav
    /// <c>First</c>/<c>FirstOrDefault</c> reducer leaf (EF-449), already staged by
    /// <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c> at emit time. Looks the answer up by ALIAS
    /// against <see cref="MongoQueryExpression.CorrelatedReducerLeaves"/> (the emit side's own committed result)
    /// rather than re-deriving admissibility here — same discipline as
    /// <see cref="TryGetNativeDocumentConstructionLeaf"/>. <paramref name="leaf"/> is not consumed by the caller
    /// today (the caller only needs the boolean to decide whether to register the whole node opaquely); it is
    /// still returned for symmetry with that sibling method and in case a future caller needs the staged
    /// <c>Lookup</c>/<c>ThrowOnEmpty</c> detail.
    /// </summary>
    private bool TryGetCorrelatedReducerLeaf(Expression expression, out MongoCorrelatedReducerLeaf leaf)
    {
        leaf = null;

        if (_queryExpression.Select.Route != NativeRoute.Projection)
        {
            return false;
        }

        var memberName = GetCurrentProjectionMember().Last?.Name;
        if (memberName is null)
        {
            return false;
        }

        var alias = _queryExpression.Select.TryGetProjectionAlias(memberName, out var overriddenAlias)
            ? overriddenAlias
            : memberName;

        foreach (var candidate in _queryExpression.CorrelatedReducerLeaves)
        {
            if (candidate.Alias == alias)
            {
                leaf = candidate;
                return true;
            }
        }

        return false;
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
            // A whole-root-entity leaf inside a NATIVE projection ($project emits {"c": "$$ROOT"}).
            //
            // WHY THE TWO GATES CANNOT DIVERGE SILENTLY (recorded at the final review, because the emit and bind
            // gates are deliberately spelled differently and a reader will wonder). The EMIT side
            // (NativeProjectionBinder.TryTranslateLeaf) admits the leaf on CLR-TYPE EQUALITY against the root
            // entity type plus parameter identity; this BIND side keys on entity-type IDENTITY plus
            // `Index: null`. The `Index: null` conjunct is what closes the only interesting gap: an
            // index-BOUND root shaper arises solely for a join's inner entity (rebound by index during join
            // translation), and that case is routed to the pre-existing StructuralTypeShaperExpression arm
            // below — including when the join's inner entity type IS the root type (a self-referencing
            // navigation), which is precisely the shape a CLR-type check alone could not tell apart. Any other
            // conceivable divergence fails LOUDLY rather than silently: if this arm did not fire for a leaf the
            // emit side projected as "$$ROOT", the shaper would look for named elements that the $project never
            // emitted and materialization would throw on the required `_id` being absent — not return a wrong
            // or null entity. So a mismatch is a hard failure, never silent wrong data.
            case StructuralTypeShaperExpression nativeRootShaper
                when _queryExpression.Select.Route == NativeRoute.Projection
                     && nativeRootShaper.StructuralType == _queryExpression.CollectionExpression.EntityType
                     && nativeRootShaper.ValueBufferExpression is ProjectionBindingExpression { Index: null } rootBinding:
                {
                    var entityProj = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
                        rootBinding.ProjectionMember);
                    var member = GetCurrentProjectionMember();
                    _projectionMapping[member] = entityProj;
                    return nativeRootShaper.Update(
                        new ProjectionBindingExpression(_queryExpression, member, typeof(ValueBuffer)));
                }

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
                // A whole-root-entity leaf in a native projection is bound by ProjectionMember
                // (Index == null, per site 1 above), so resolve through the LOCAL mapping in that case —
                // _queryExpression.GetMappedProjection is not populated yet; ReplaceProjectionMapping only
                // copies _projectionMapping into the query expression at the end of Translate.
                case ProjectionBindingExpression { Index: null } memberBoundBinding:
                    innerEntityProjection = (EntityProjectionExpression)(
                        _projectionMapping.TryGetValue(memberBoundBinding.ProjectionMember, out var localEntityProj)
                            ? localEntityProj
                            : _queryExpression.GetMappedProjection(memberBoundBinding.ProjectionMember));
                    break;

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

                // EF-427 item 1: the same stranded-rebuild gap as the Count/LongCount arm immediately above,
                // for the bare (no-predicate) First/FirstOrDefault/Single/SingleOrDefault/Any reducers — see
                // that arm's own comment for the full mechanism (why `break` not `return null`, why the
                // fall-through's second Visit is not universally safe, why this is unreachable for a NATIVE
                // reducer). Deliberately narrow: the PREDICATED overloads (FirstWithPredicate etc.) and
                // Sum/Min/Max/Average (per-numeric-type overloads, not a single generic-in-TSource method) are
                // out of scope for this task.
                //
                // MEASURED DIFFERENCE FROM THE Count/LongCount ARM'S SHAPE: unlike Count, EF Core does NOT
                // fuse a preceding member-access Select into these five reducers. `Select(b =>
                // b.Posts.First().Heading)` nav-expands to `b.Posts.Select(o => o.Heading).First()` — the
                // trailing member access is pushed INTO the source as its own Select, so `visitedSource` here
                // is the ALREADY-REBUILT `Enumerable.Select(shaper, lambda)` call the adjacent Select arm
                // above just produced (type `IEnumerable<THeading>`), not a bare `CollectionShaperExpression`
                // (type `List<Post>`). A guard that only matched `CollectionShaperExpression` (mirroring the
                // Count arm literally) declines this shape and it keeps throwing — measured directly: the
                // bare bodyless `Select(b => b.Posts.Any())` (no trailing member access, so no Select is
                // pushed in) DOES reach here as a raw `CollectionShaperExpression`, but the four reducers
                // this ticket's own repro exercises (immediately followed by `.Heading`) do not. So the
                // source-shape check below is widened to "assignable to the Enumerable equivalent's own
                // `IEnumerable<TSource>` parameter, but NOT already assignable to this Queryable method's own
                // `IQueryable<TSource>` parameter" — see <see cref="TryRebuildAsEnumerableSource"/> — which
                // admits both shapes uniformly while staying exactly as narrow as the Count arm's own
                // discipline: it still only ever fires on a source that would otherwise crash today (a
                // genuine untouched `IQueryable<TSource>` source stays assignable to the Queryable parameter
                // and is correctly left to the ordinary fall-through, unchanged).
                case nameof(Queryable.First)
                    when genericMethod == QueryableMethods.FirstWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var firstSource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.FirstWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        firstSource);

                case nameof(Queryable.FirstOrDefault)
                    when genericMethod == QueryableMethods.FirstOrDefaultWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var firstOrDefaultSource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.FirstOrDefaultWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        firstOrDefaultSource);

                case nameof(Queryable.Single)
                    when genericMethod == QueryableMethods.SingleWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var singleSource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.SingleWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        singleSource);

                case nameof(Queryable.SingleOrDefault)
                    when genericMethod == QueryableMethods.SingleOrDefaultWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var singleOrDefaultSource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.SingleOrDefaultWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        singleOrDefaultSource);

                case nameof(Queryable.Any)
                    when genericMethod == QueryableMethods.AnyWithoutPredicate:
                    if (!TryRebuildAsEnumerableSource(method, visitedSource, methodCallExpression.Arguments[0], out var anySource))
                    {
                        break;
                    }

                    return Expression.Call(
                        EnumerableMethods.AnyWithoutPredicate.MakeGenericMethod(method.GetGenericArguments()),
                        anySource);

                // The same rebuild as the arm immediately above, for the PREDICATED Count/LongCount
                // overloads — this delivers the BARE filtered-count projection,
                // `Select(b => b.Posts.Count(p => ...))`, and only that shape. A NATIVE filtered-count
                // projection never reaches here: the Route == NativeRoute.Projection registration earlier in
                // VisitMethodCall (gated by IsCanonicalCount, which admits both arities) already claims the
                // predicated overloads and pushes the count into $project instead — so this arm only ever
                // sees a shape that is NOT going native.
                //
                // "Bare spelling" here means a bare selector BODY REACHABLE THROUGH A PURE ARITHMETIC/CAST
                // SPINE (see IsReachableThroughArithmeticSpine below — EF-427 item 2 widened this from a bare
                // exact-identity check), not merely a bare TOP node — narrower than the arm above it.
                // `Select(b => b.Posts.Count * 2)` folds client-side (the unfiltered arm has no
                // `_translatedRootExpression` reachability check at all — any shaper source is enough). BEFORE
                // EF-427, the filtered analogue, `Select(b => b.Posts.Count(p => ...) * 2)`, hard-failed in
                // every mode: the Count call was an OPERAND of the top-level `*`, not the selector body
                // itself, so the OLD exact-identity check failed and this arm declined with no graceful
                // fallback (see the arm's own `IsReachableThroughArithmeticSpine` doc comment for the widened
                // mechanism). Note the asymmetry this used to record, now closed for the arithmetic-spine
                // case specifically (a filtered count behind some OTHER operator, or wrapped in a `new {...}`,
                // is untouched by this widening and still hard-fails/goes native the same way it always did):
                // the UNFILTERED `Select(b => b.Posts.Count * 2)` is a graceful decline with correct values in
                // the two fallback modes, while this FILTERED spelling used to be a hard fail in all three,
                // and the WRAPPED filtered form is native. The `new {...}` is the difference, not the
                // arithmetic.
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
                // MUST BE REACHABLE FROM THE SELECTOR BODY THROUGH A PURE ARITHMETIC/CAST SPINE ONLY, not a
                // leaf nested inside a WRAPPED anonymous/DTO projection, and not reachable through any OTHER
                // kind of node (a method call, a member access, a conditional, ...). A WRAPPED projection's
                // element predicate can decline to Fallback for reasons unrelated to this arm
                // (correlated-beyond-element, a non-renderable predicate like `StartsWith`, a primitive-element
                // collection, or the structurally distinct `Where(pred).Count()` shape) — see
                // NativeOwnedCollectionFilteredCountTests' pinned `..._still_hard_fail(s)_in_every_mode` tests.
                // Those shapes reach this SAME switch arm too (Route == Fallback for a DIFFERENT reason than a
                // bare/arithmetic-reachable selector body), and `visitedSource` is STILL a genuine
                // CollectionShaperExpression for them (visiting a real owned-collection navigation produces
                // one regardless of Route) — so the shaper-type check alone does not distinguish a
                // bare/arithmetic-reachable spelling from an unrelated decline residual.
                // `IsReachableThroughArithmeticSpine(_translatedRootExpression, methodCallExpression)` (the
                // top-level expression this Translate() call started with — see its own doc comment) does: it
                // is true only when this Count call is the selector body itself OR reachable from it through
                // nothing but arithmetic/cast nodes. For a WRAPPED shape the Count call is nested inside a
                // NewExpression/MemberInit — a node kind the spine walk does not recurse into — so reachability
                // fails and this arm declines as before.
                //
                // NOT REDUNDANT WITH `ContainsShaperReference` BELOW — the two guards protect DIFFERENT
                // residual shapes. The reachability guard alone does NOT restore the WRAPPED CORRELATED residual
                // — a proxy for "does this predicate reference the enclosing shaper", not that
                // property itself; see `ContainsShaperReference`'s own doc comment for why a BARE correlated
                // predicate slips past reachability but is caught by the structural check. (EF-421 Task 7:
                // SelfParam now reaches NativeProjectionBinder unconditionally, so a WRAPPED correlated
                // predicate — formerly pinned by `NativeOwnedCollectionFilteredCountTests`'s
                // `Correlated_primitive_and_where_count_filtered_projections_still_hard_fail_in_every_mode`'s
                // first row, now split out to that file's `Correlated_count_filtered_projection_goes_native_
                // EF421` — goes native upstream (Route stays Projection) and never reaches this Fallback-only
                // arm at all any more; this guard's own reasoning is otherwise unchanged and is exercised now
                // by any OTHER Fallback-reaching wrapped correlated shape.) Conversely,
                // `ContainsShaperReference` alone does NOT restore the WRAPPED NON-RENDERABLE residual
                // (`Non_renderable_element_predicate_filtered_projection_still_hard_fails_in_every_mode`, the
                // `StartsWith` case): that predicate references only its own element parameter `p` — no shaper
                // node, no query parameter — so nothing about it is structurally distinguishable from the bare
                // spelling except that the Count call is nested inside a `new {...}` rather than being the whole
                // selector body. Only the reachability check catches THAT one. Both guards stay.
                // Widened per EF-427 item 2: the filtered Count call need not BE the selector body — it may be
                // any operand reachable from the root through a pure arithmetic/cast spine
                // (Select(b => b.Posts.Count(pred) * 2), Select(b => (b.Posts.Count(pred) + 1) * 2), etc.).
                // "No interposed shaper reference" means every node on the path from root to this call is
                // itself just arithmetic/cast — never another operator that would need its own
                // CollectionShaperExpression (which this rewrite has no way to thread through an arbitrary
                // operand position). IsReachableThroughArithmeticSpine subsumes the old exact-identity check
                // (ReferenceEquals(root, target) is its base case), so this replaces that check outright rather
                // than adding a second arm above it.
                case nameof(Queryable.Count)
                    when genericMethod == QueryableMethods.CountWithPredicate:
                case nameof(Queryable.LongCount)
                    when genericMethod == QueryableMethods.LongCountWithPredicate:
                    if (visitedSource is not CollectionShaperExpression filteredCountShaper
                        || !IsReachableThroughArithmeticSpine(_translatedRootExpression, methodCallExpression))
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

            // EF-425. No case above claimed this Queryable operator, so it is about to reach the generic
            // fall-through at the bottom of this method. That fall-through re-Visits every argument (including
            // Arguments[0], which the `visitedSource` above already visited once) and then calls
            // `methodCallExpression.Update(...)` — rebuilding the SAME Queryable call around the visited
            // source. Both halves of that are unsound once the source has been visited into a MATERIALIZED
            // collection expression, and each half produced its own bare, unnamed crash in EVERY query mode
            // (Native, DriverLinq and NativeOnly alike — this runs at translation time, before the compile-time
            // gate ever reads MongoQueryMode):
            //
            //   * `Update` re-validates argument assignability through Expression.Call's own BCL checks, and a
            //     Queryable operator's first parameter is `IQueryable<T>`, which neither a
            //     CollectionShaperExpression (typed as the navigation's List<T>) nor the `Enumerable.Select`
            //     call the adjacent Select case rebuilds (typed IEnumerable<T>) is assignable to. MatchTypes
            //     cannot rescue it: it only inserts a Convert when the target type has NO item type, and
            //     `IQueryable<T>` always has one. Measured today on `b.Posts.Take(2).Select(p => p.Heading)`
            //     and the `Reverse` analogue (EF pushes the projection INSIDE those two, so the source the
            //     operator sees is the raw collection shaper): `ArgumentException: Expression of type
            //     'List<Post>' cannot be used for parameter of type 'IQueryable<Post>'`.
            //   * The second Visit of Arguments[0] is not side-effect-free when that subtree is an
            //     owned-collection `Select`: the Select case above registers its lambda parameter in
            //     `_collectionShaperMapping` with a non-idempotent `.Add`, so visiting it twice throws
            //     `ArgumentException: An item with the same key has already been added. Key: p`. Measured today
            //     on `b.Posts.Select(p => p.Heading).Distinct()` and the `DefaultIfEmpty` analogue (neither can
            //     be pushed inside the projection, so the source they see IS the rebuilt Select). This is the
            //     hazard the Count arms' `break` comments above already flag as the limit of their safety.
            //
            // Declining here — BEFORE the fall-through mutates anything — converts both into the same clean,
            // named `InvalidOperationException` every other unsupported shape in this file produces, and
            // `Print()` names the operator and the navigation (`Concat` already landed here by a different
            // route and produced exactly this exception, which is why it never showed the crash).
            //
            // Deliberately a THROW, not `return null`: a null return folds through
            // `MatchTypes(null, targetType)` to `Expression.Default(targetType)` — a silent null/empty
            // collection for a collection-typed projection leaf — which is wrong data rather than a decline.
            // Same reasoning the Count arms above give for using `break` rather than `return null`.
            //
            // Deliberately a STRUCTURAL guard rather than one case per operator. The failing family is not a
            // fixed list of five methods: it is "any Queryable operator with no case of its own whose source
            // has been rewritten into a materialized collection", so keying on assignability catches Skip,
            // ElementAt, Where and the rest of the long tail on the same terms. It cannot fire on a shape that
            // survives the fall-through today: `Expression.Call` (and hence `Update`) enforces this exact
            // assignability itself, so any input that reaches and survives `Update` already satisfies the
            // condition below and is left untouched.
            //
            // INSTRUMENTED, not assumed (temporary trace at this line, removed): across the whole functional
            // Query suite the only inputs that reach this point at all are the four crashing shapes —
            // `Distinct`/`DefaultIfEmpty` arriving with an `IEnumerable<string>`-typed rebuilt Select, and
            // `Take`/`Reverse` arriving with a `List<Post>`-typed CollectionShaperExpression. Notably the
            // wrapped filtered-`Count` declines whose comments above describe this fall-through never reach
            // here (their `b.Posts.Count(pred)` binds to `Enumerable`, not `Queryable`, so this whole block is
            // skipped for them) — which is why the EF-365 graceful-fallback shapes are unaffected.
            if (visitedSource != null
                && !ReferenceEquals(visitedSource, methodCallExpression.Arguments[0])
                && !method.GetParameters()[0].ParameterType.IsAssignableFrom(visitedSource.Type))
            {
                throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
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
            // NOTE: same unconditional .Index.Value deref shape as the site above (VisitMethodCall), which
            // EF-412's Index: null bindings could in principle trip. UPGRADED FROM AN OPEN QUESTION TO A BOUND
            // at the final review: a whole-root-entity leaf (or any other projection leaf) does NOT reach here.
            // This visitor's own Visit override matches `case MemberExpression` unconditionally and returns a
            // ProjectionBindingExpression there, so a MemberExpression is never handed to base.Visit and
            // VisitMember is never dispatched for it; the only base.Visit calls that could carry a foreign node
            // are for a MaterializeCollectionNavigationExpression's Subquery (a method-call chain, never a
            // member access) and the default arm (which a MemberExpression cannot reach, being matched earlier).
            // MEASURED, not just read: with `throw` inserted as VisitMember's first statement the entire EF10
            // suite stayed green (947 unit + 4599 spec + 2961 functional, 0 failures), i.e. no test in the tree
            // dispatches here at all. Re-verify by the same mutation if Visit's MemberExpression arm ever gains
            // a guard that lets a member access fall through.
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
    /// EF-427 item 1: decides whether <paramref name="visitedSource"/> — the already-visited
    /// <c>Arguments[0]</c> of a bare <c>Queryable.First</c>/<c>FirstOrDefault</c>/<c>Single</c>/
    /// <c>SingleOrDefault</c>/<c>Any</c> call — is a shape that would otherwise crash the generic
    /// fall-through's <c>Expression.Call</c>/<c>Update</c> validation (the same "is this the crash-prone
    /// shape" question the Count/LongCount arm above answers with a plain
    /// <c>visitedSource is CollectionShaperExpression</c> check), and if so, hands back the source to rebuild
    /// the <see cref="Enumerable"/> equivalent against.
    /// </summary>
    /// <remarks>
    /// Wider than a bare <c>CollectionShaperExpression</c> check because — unlike <c>Count</c>/<c>LongCount</c>
    /// — EF Core does not fuse a trailing member-access <c>Select</c> into these five reducers:
    /// <c>b.Posts.First().Heading</c> nav-expands to <c>b.Posts.Select(o => o.Heading).First()</c>, so
    /// <paramref name="visitedSource"/> here is often the ALREADY-REBUILT <c>Enumerable.Select(shaper, lambda)</c>
    /// call the adjacent <c>Select</c> case produces (type <c>IEnumerable&lt;TResult&gt;</c>), not the raw
    /// <c>CollectionShaperExpression</c> (type <c>List&lt;T&gt;</c>) a bodyless call like
    /// <c>b.Posts.Any()</c> still produces directly. Both need rebuilding; neither is caught by a
    /// type-specific check for the other.
    /// </remarks>
    /// <param name="method">The constructed generic <c>Queryable</c> method being visited.</param>
    /// <param name="visitedSource">The already-visited <c>Arguments[0]</c>.</param>
    /// <param name="originalSource">The UNVISITED <c>Arguments[0]</c>, to detect "nothing changed".</param>
    /// <param name="enumerableSource">On success, <paramref name="visitedSource"/> itself, ready to pass as the
    /// <see cref="Enumerable"/> equivalent's source argument.</param>
    /// <returns>
    /// <see langword="false"/> — leave this case's <c>break</c> to fall through UNCHANGED — when: (a)
    /// <paramref name="visitedSource"/> is null; (b) nothing was rewritten (<c>ReferenceEquals</c> the
    /// original argument), i.e. a genuine, still-untouched <c>IQueryable&lt;TSource&gt;</c> source that the
    /// ordinary generic fall-through already handles correctly; (c) <paramref name="visitedSource"/> is
    /// STILL assignable to this Queryable method's own <c>IQueryable&lt;TSource&gt;</c> parameter, i.e. not a
    /// crash-prone shape at all; or (d) — defence in depth — <paramref name="visitedSource"/> is not even
    /// assignable to the <see cref="Enumerable"/> equivalent's own <c>IEnumerable&lt;TSource&gt;</c> parameter,
    /// in which case rebuilding would just trade today's <see cref="InvalidOperationException"/> (from the
    /// fall-through's own decline further down) for a worse, confusing <see cref="ArgumentException"/> from
    /// <see cref="Expression.Call(MethodInfo, Expression[])"/>'s own BCL validation — so this declines instead,
    /// exactly the same "clean decline over a confusing crash" preference the filtered-Count arm's
    /// <c>ContainsQueryParameter</c> guard documents.
    /// </returns>
    private static bool TryRebuildAsEnumerableSource(
        MethodInfo method, Expression visitedSource, Expression originalSource, out Expression enumerableSource)
    {
        enumerableSource = null;

        if (visitedSource is null || ReferenceEquals(visitedSource, originalSource))
        {
            return false;
        }

        if (method.GetParameters()[0].ParameterType.IsAssignableFrom(visitedSource.Type))
        {
            return false;
        }

        var elementType = method.GetGenericArguments()[0];
        if (!typeof(IEnumerable<>).MakeGenericType(elementType).IsAssignableFrom(visitedSource.Type))
        {
            return false;
        }

        enumerableSource = visitedSource;
        return true;
    }

    /// <summary>
    /// EF-427 item 2: true when <paramref name="target"/> — a filtered <c>Count</c>/<c>LongCount</c> call — is
    /// reachable from <paramref name="root"/> (the whole selector body,
    /// <see cref="_translatedRootExpression"/>) through a spine of nothing but arithmetic (<c>+ - * / %</c>)
    /// and numeric <c>Convert</c> nodes, e.g. <c>Select(b => b.Posts.Count(pred) * 2)</c> or
    /// <c>Select(b => (b.Posts.Count(pred) + 1) * 2)</c>.
    /// </summary>
    /// <remarks>
    /// This subsumes the old exact-identity check: the base case, <c>ReferenceEquals(root, target)</c>, is
    /// exactly what a bare <c>Select(b => b.Posts.Count(pred))</c> selector body satisfies. Every other node
    /// kind (a method call, a member access, a conditional, anything that is not plain arithmetic/cast)
    /// declines — "no interposed shaper reference" means every node on the path from root to target is
    /// ITSELF just arithmetic/cast, never another operator that would need its own
    /// <see cref="CollectionShaperExpression"/> the way the filtered-count rebuild below needs one for
    /// <paramref name="target"/>. This function is a pure, read-only structural walk over the RAW (not yet
    /// visited) expression tree — it does not call <see cref="Visit"/> and has no side effects, so it cannot
    /// introduce a double-registration hazard (e.g. the <c>_collectionShaperMapping.Add</c> non-idempotence
    /// the Count/LongCount arms' own comments warn about); the actual visiting of every operand still happens
    /// exactly once each, via the ordinary recursive <see cref="ExpressionVisitor.VisitBinary"/>/<see
    /// cref="ExpressionVisitor.VisitUnary"/> dispatch, unaffected by this check. If the same filtered-Count
    /// call is reachable more than once (e.g. two syntactically distinct predicates on either side of a `+`),
    /// each occurrence is a genuinely distinct <see cref="MethodCallExpression"/> node that reaches this
    /// switch's filtered-Count case independently and is rebuilt independently and correctly — this function
    /// is a pure predicate re-evaluated per call site, not shared mutable state. It also cannot change
    /// anything about the EF-425 interposed-operator family (<c>Distinct</c>/<c>Take</c>/<c>Reverse</c>/
    /// <c>DefaultIfEmpty</c>/<c>Concat</c> between an owned-collection <c>Select</c> and a terminal operator):
    /// that family's decline happens while visiting <paramref name="target"/>'s OWN <c>Arguments[0]</c> (the
    /// collection source the count call is filtering over), a step this function never touches — it only
    /// decides whether <paramref name="target"/> ITSELF is reachable from the root, never how its argument is
    /// visited.
    /// </remarks>
    private static bool IsReachableThroughArithmeticSpine(Expression root, MethodCallExpression target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        return root switch
        {
            BinaryExpression
            {
                NodeType: ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
                    or ExpressionType.Divide or ExpressionType.Modulo
            } binary
                => IsReachableThroughArithmeticSpine(binary.Left, target) || IsReachableThroughArithmeticSpine(binary.Right, target),
            UnaryExpression { NodeType: ExpressionType.Convert } unary
                => IsReachableThroughArithmeticSpine(unary.Operand, target),
            _ => false
        };
    }

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
