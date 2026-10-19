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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Binds owned-collection <c>SelectMany</c> to a native <c>$unwind</c> + <c>$project</c>, across the two
/// user-authored shapes EF's nav-expansion can produce.
/// </summary>
/// <remarks>
/// <see cref="TryBind"/> handles the INNER-<c>Select</c> form —
/// <c>o => o.Items.AsQueryable().Select(i => new { o.X, i.Y })</c> — where the projection is nested inside
/// the collection selector itself. <see cref="TryBindBareNavUnwind"/> +
/// <see cref="TryBindTransparentIdentifierProjection"/> together handle the explicit-result-selector /
/// query-syntax form — <c>SelectMany(o => o.Items, (o, i) => new { o.X, i.Y })</c> / <c>from o in q from i
/// in o.Items select new { o.X, i.Y }</c> — which normalizes to a BARE owned-nav collection selector (no
/// nested <c>Select</c>) plus a SEPARATE trailing <c>Select</c> over the
/// <c>TransparentIdentifier(Outer, Inner)</c> result: <see cref="TryBindBareNavUnwind"/> sets
/// <see cref="MongoSelectDefinition.UnwindSource"/> from the bare nav alone, and
/// <see cref="TryBindTransparentIdentifierProjection"/> — invoked separately, from the trailing <c>Select</c>
/// — binds that Select's <c>ti.Outer</c>/<c>ti.Inner</c> member accesses into
/// <see cref="MongoSelectDefinition.Projection"/>. All three binders resolve outer (closed-over) members to
/// root field refs and inner (collection-element) members to the unwound element, prefixed with the unwind
/// path, via two structurally separate <see cref="MongoExpressionTranslator"/>s — see the
/// scope-by-parameter-identity invariant in <c>Query/AGENTS.md</c>. Each returns <see langword="false"/>
/// (select/projection untouched) for any shape outside its own scope; for <see cref="TryBind"/> and
/// <see cref="TryBindBareNavUnwind"/> the caller then returns <see langword="null"/> and EF hard-fails
/// translation.
/// </remarks>
internal static class NativeSelectManyBinder
{
    internal static bool TryBind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        // Body must be Queryable.Select(<source>, innerLambda).
        if (collectionSelector.Body is not MethodCallExpression
            {
                Method: { Name: nameof(System.Linq.Queryable.Select), DeclaringType: var selDecl },
                Arguments: [var selectSource, var innerLambdaArg]
            }
            || selDecl != typeof(System.Linq.Queryable))
            return false;

        // <source> must resolve to the outer parameter's owned-collection navigation. EF's nav-expansion
        // rewrites navigation access to EF.Property(o, "Nav"), so both that and a plain MemberExpression
        // must be accepted here. Peel any user Where(...) layers off the owned nav first — owned collections
        // are a bare member access, so every Where here is an inner-element user filter (no FK correlation).
        var userPredicates = new List<LambdaExpression>();
        var navExpr = PeelOwnedInnerWhere(selectSource, userPredicates);
        if (!TryGetMemberAccess(navExpr, out var navRoot, out var navName) || !ReferenceEquals(navRoot, outerParam))
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var navigation = outerEntityType.FindNavigation(navName);
        if (navigation is not { IsCollection: true } || !navigation.TargetEntityType.IsOwned())
            return false;
        if (navigation.TargetEntityType.GetContainingElementName() is not { } unwindPath)
            return false;

        var innerLambda = innerLambdaArg.UnwrapLambdaFromQuote();
        if (innerLambda.Parameters.Count != 1)
            return false;
        var innerParam = innerLambda.Parameters[0];

        if (!TryReadProjection(innerLambda.Body, out var members))
            return false;

        var outerTranslator = new MongoExpressionTranslator(outerEntityType);
        var innerTranslator = new MongoExpressionTranslator(navigation.TargetEntityType);
        var projections = new List<MongoProjection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, argExpr) in members)
        {
            if (!TryGetMemberAccess(argExpr, out var root, out _))
                return false;

            bool isInner;
            if (ReferenceEquals(root, outerParam)) isInner = false;
            else if (ReferenceEquals(root, innerParam)) isInner = true;
            else return false;

            if (!TryTranslateScopedField(outerTranslator, innerTranslator, unwindPath, argExpr, isInner, out var field))
                return false;

            if (!seen.Add(alias)) return false;
            projections.Add(new MongoProjection(alias, field));
        }

        if (!TryBuildOwnedInnerFilter(userPredicates, navigation.TargetEntityType, unwindPath, outerParam, outerEntityType, out var filter))
            return false;

        var unwind = MongoUnwindSource.Owned(unwindPath, navigation.TargetEntityType);
        unwind.Filter = filter;
        mongoQ.Select.AddUnwindSource(unwind);
        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        return true;
    }

    /// <summary>
    /// Binds the BARE-nav collection-selector shape of an owned-collection <c>SelectMany</c> —
    /// <c>o =&gt; o.Items.AsQueryable()</c> (or <c>EF.Property(o,"Items")</c>), with NO nested <c>Select</c> —
    /// which is what EF's nav-expansion produces for both the explicit-result-selector form
    /// (<c>SelectMany(o =&gt; o.Items, (o,i) =&gt; ...)</c>) and the query-syntax equivalent.
    /// </summary>
    /// <remarks>
    /// Sets <see cref="MongoSelectDefinition.UnwindSource"/> only — the real projection is bound later, by
    /// <see cref="TryBindTransparentIdentifierProjection"/> against the SEPARATE trailing <c>Select</c>.
    /// Returns <see langword="false"/> (select untouched) for a nested-<c>Select</c> body (that is
    /// <see cref="TryBind"/>'s inner-<c>Select</c> form), a non-owned/reference navigation, or a
    /// non-collection navigation.
    /// </remarks>
    internal static bool TryBindBareNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        var userPredicates = new List<LambdaExpression>();
        var navExpr = PeelOwnedInnerWhere(collectionSelector.Body, userPredicates);
        if (!TryGetMemberAccess(navExpr, out var navRoot, out var navName) || !ReferenceEquals(navRoot, outerParam))
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var navigation = outerEntityType.FindNavigation(navName);
        if (navigation is not { IsCollection: true } || !navigation.TargetEntityType.IsOwned())
            return false;
        if (navigation.TargetEntityType.GetContainingElementName() is not { } unwindPath)
            return false;

        if (!TryBuildOwnedInnerFilter(userPredicates, navigation.TargetEntityType, unwindPath, outerParam, outerEntityType, out var filter))
            return false;

        var unwind = MongoUnwindSource.Owned(unwindPath, navigation.TargetEntityType);
        unwind.Filter = filter;
        mongoQ.Select.AddUnwindSource(unwind);
        return true;
    }

    /// <summary>
    /// Binds a cross-collection REFERENCE-nav <c>SelectMany</c> — <c>SelectMany(c =&gt; c.Orders, (c, o) =&gt; new
    /// {...})</c> / <c>from c in q from o in c.Orders select new {...}</c> — over a REFERENCE (non-embedded)
    /// collection navigation.
    /// </summary>
    /// <remarks>
    /// Unlike the owned bare-nav shape (<see cref="TryBindBareNavUnwind"/>), EF's nav-expansion normalizes a
    /// reference collection selector to a CORRELATED SUBQUERY, not a bare nav: <c>Queryable.Where(EntityQueryRootExpression
    /// &lt;Target&gt;, o =&gt; c.pk == o.fk)</c> (possibly wrapped in <c>AsQueryable</c>) — the target collection
    /// is queried from its own root and filtered by the FK correlation, rather than read off the outer entity
    /// directly. Recognizes that shape via <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/>
    /// (shared with <see cref="NativeProjectionBinder"/>'s projected-<c>Count</c> recognition), requiring a
    /// REFERENCE (<c>requireEmbedded: false</c>) navigation — the mirror of <see cref="TryBindBareNavUnwind"/>'s
    /// owned-only acceptance, so the two binders partition the shape space rather than overlap. On a match,
    /// registers a <c>ForceUnwind</c> <c>$lookup</c> for the navigation and sets
    /// <see cref="MongoSelectDefinition.UnwindSource"/> to a <see cref="MongoUnwindSourceKind.Reference"/> source
    /// whose scope is the lookup's <c>_lookup_&lt;Nav&gt;</c> alias — the real projection is bound later, by the
    /// unchanged <see cref="TryBindTransparentIdentifierProjection"/> against the separate trailing <c>Select</c>.
    /// </remarks>
    internal static bool TryBindReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        // Peel user-predicate Where layers. A filtered inner c.Refs.Where(p1).Where(p2) nav-expands to
        // Where(Where(Where(root, fkPred), p1), p2): the innermost Where over the query root carries the FK
        // correlation EF injects; every outer Where is an inner-element-only user filter. (A single Where whose
        // predicate is fkPred && userPred — the "folded" shape — is split below by TrySplitCorrelation.)
        var body = UnwrapAsQueryable(collectionSelector.Body);
        var userPredicates = new List<LambdaExpression>();
        while (body is MethodCallExpression
               {
                   Method: { Name: nameof(System.Linq.Queryable.Where), DeclaringType: var outerDecl },
                   Arguments: [var outerSource, var outerPredArg]
               }
               && outerDecl == typeof(System.Linq.Queryable)
               && UnwrapAsQueryable(outerSource) is not EntityQueryRootExpression)
        {
            userPredicates.Add(outerPredArg.UnwrapLambdaFromQuote());
            body = UnwrapAsQueryable(outerSource);
        }

        if (body is not MethodCallExpression
            {
                Method: { Name: nameof(System.Linq.Queryable.Where), DeclaringType: var whereDecl },
                Arguments: [EntityQueryRootExpression root, var predicateArg]
            }
            || whereDecl != typeof(System.Linq.Queryable))
            return false;

        var predicate = predicateArg.UnwrapLambdaFromQuote();
        if (predicate.Parameters.Count != 1)
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;

        // Isolate the FK correlation (→ the reference navigation) from any user conjunct folded into the
        // innermost predicate; the shared matcher's own reject-extra-conjunct contract is untouched.
        if (!TrySplitCorrelation(predicate.Body, outerEntityType, outerParam, root.EntityType,
                out var navigation, out var foldedUserBody))
            return false;

        // Translate each user filter (peeled Where layers + any folded conjunct) and AND the results into one
        // predicate. A layer referencing only the inner element translates against the inner target entity
        // type, prefixed with the $lookup scope; a layer also referencing outer members beyond the FK is routed
        // to the two-scope translator instead. Declines cleanly, with no partial mutation, if either fails.
        var scope = LookupExpression.GetLookupAlias(navigation);
        var innerTranslator = new MongoExpressionTranslator(navigation.TargetEntityType);
        MongoExpression? filter = null;

        if (foldedUserBody != null)
        {
            if (!TryTranslateReferenceFilterLayer(
                    foldedUserBody, innerTranslator, navigation.TargetEntityType, scope, outerParam, outerEntityType, out var foldedExpr))
                return false;
            filter = foldedExpr;
        }

        foreach (var userPredicate in userPredicates)
        {
            if (userPredicate.Parameters.Count != 1
                || !TryTranslateReferenceFilterLayer(
                    userPredicate.Body, innerTranslator, navigation.TargetEntityType, scope, outerParam, outerEntityType, out var userExpr))
                return false;
            filter = filter == null
                ? userExpr
                : new MongoBinaryExpression(MongoBinaryOperator.AndAlso, filter, userExpr);
        }

        var lookup = new LookupExpression(navigation, forceUnwind: true);
        // AddLookup dedupes on the alias (As) — if a same-nav Include-registered lookup were already pending,
        // this call would be a no-op and UnwindSource.Lookup below would point at an instance not actually in
        // the pending list. That collision can't happen here: a reference SelectMany is always projected-only
        // (a bare-entity trailing selector hard-declines earlier), and EF Core drops any Include not applied to
        // the query's final materialized entity, so no same-nav Include lookup can be pending to collide with.
        mongoQ.AddLookup(lookup);
        var unwind = MongoUnwindSource.Reference(scope, navigation.TargetEntityType, lookup);
        unwind.Filter = filter;
        mongoQ.Select.AddUnwindSource(unwind);
        return true;
    }

    /// <summary>
    /// Binds the SECOND level of a nested (2-level) cross-collection reference <c>SelectMany</c> —
    /// <c>from o in q from m in o.Mids from l in m.Leaves select ...</c>.
    /// </summary>
    /// <remarks>
    /// EF's nav-expansion produces this as a second, sequentially-chained <c>Queryable.Where(EntityQueryRootExpression
    /// &lt;Leaf&gt;, l => ti.Inner.Id == l.MidId)</c> correlated subquery — structurally identical to the
    /// single-level shape <see cref="TryBindReferenceNavUnwind"/> already parses, except the correlation's
    /// outer-key side is a transparent-identifier-rooted member access <c>ti.Inner.&lt;pk&gt;</c> (<c>ti</c> is
    /// this SelectMany's own outer parameter, bound by nav-expansion to level 1's <c>TransparentIdentifier(Outer,
    /// Inner)</c> result) rather than a bare parameter. Rather than teach <see cref="NativeCorrelationMatcher"/>
    /// a new shape, this rewrites every <c>ti.Inner</c> occurrence in the predicate onto a synthetic parameter
    /// of the level-1 target entity type first, then reuses
    /// <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/> unchanged.
    /// <para>
    /// Requires the caller to have already confirmed exactly one prior REFERENCE unwind source
    /// (<see cref="MongoSelectDefinition.IsSingleReferenceUnwindTerminalOnly"/>). Resolves the navigation off
    /// that source's <see cref="MongoUnwindSource.InnerEntityType"/> (the level-1 target, e.g. Mid), registers
    /// a second <c>ForceUnwind</c> <see cref="LookupExpression"/> whose <see cref="LookupExpression.LocalField"/>
    /// is overridden to be scoped under the level-1 source's own <see cref="MongoUnwindSource.InnerScopePath"/>
    /// (e.g. <c>_lookup_Mids._id</c>), and appends a second <see cref="MongoUnwindSource"/>. No partial
    /// mutation on decline.
    /// </para>
    /// <para>
    /// Unfiltered only: unlike <see cref="TryBindReferenceNavUnwind"/> this does not peel outer <c>Where</c>
    /// layers — an inner filter at level 2 nav-expands to an outer <c>Where</c> wrapping the FK-correlation
    /// <c>Where</c>, which does not match the single-<c>Where</c> shape checked here, so a filtered level 2
    /// declines structurally.
    /// </para>
    /// </remarks>
    internal static bool TryBindNestedReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var sources = mongoQ.Select.UnwindSources;
        if (sources.Count != 1 || sources[0].Kind != MongoUnwindSourceKind.Reference)
            return false;
        var level1Source = sources[0];

        var ti = collectionSelector.Parameters[0];
        var body = UnwrapAsQueryable(collectionSelector.Body);

        if (body is not MethodCallExpression
            {
                Method: { Name: nameof(System.Linq.Queryable.Where), DeclaringType: var whereDecl },
                Arguments: [EntityQueryRootExpression root, var predicateArg]
            }
            || whereDecl != typeof(System.Linq.Queryable))
            return false;

        var predicate = predicateArg.UnwrapLambdaFromQuote();
        if (predicate.Parameters.Count != 1)
            return false;

        // Rewrite every `ti.Inner` occurrence onto a synthetic parameter of the level-1 target entity type
        // (e.g. Mid), so the single-level matcher (which expects a bare-parameter-rooted outer side)
        // recognizes the correlation unchanged.
        var level1Param = Expression.Parameter(level1Source.InnerEntityType.ClrType, "l1");
        var rewritten = new TransparentIdentifierInnerRewriter(ti, level1Param).Visit(predicate.Body);

        if (!NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                rewritten, level1Source.InnerEntityType, level1Param, root.EntityType, requireEmbedded: false, out var navigation))
            return false;

        var scope2 = LookupExpression.GetLookupAlias(navigation);
        var lookup2 = new LookupExpression(navigation, forceUnwind: true);
        lookup2.LocalField = level1Source.InnerScopePath + "." + lookup2.LocalField;
        mongoQ.AddLookup(lookup2);
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Reference(scope2, navigation.TargetEntityType, lookup2));
        return true;
    }

    /// <summary>
    /// Rewrites every <c>tiParam.Inner</c> occurrence onto <paramref name="replacement"/>. Used by
    /// <see cref="TryBindNestedReferenceNavUnwind"/> to turn the level-2 correlation's
    /// transparent-identifier-rooted outer side (<c>ti.Inner.&lt;pk&gt;</c>) into a plain bare-parameter-rooted
    /// member access <see cref="NativeCorrelationMatcher"/> already recognizes.
    /// </summary>
    private sealed class TransparentIdentifierInnerRewriter(ParameterExpression tiParam, ParameterExpression replacement)
        : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
            => node.Expression == tiParam && node.Member.Name == "Inner"
                ? replacement
                : base.VisitMember(node);
    }

    /// <summary>
    /// Resolves the FK-correlated reference navigation from the innermost <c>Where</c> predicate, isolating it
    /// from any inner-element user filter folded into the same predicate (<c>fkPred &amp;&amp; userPred</c>).
    /// The shared <see cref="NativeCorrelationMatcher"/> is only ever fed the isolated FK-correlation
    /// expression, so its reject-extra-conjunct contract (which keeps a filtered <c>Count</c> on fallback) is
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// The folded branch is defensive-only: EF's nav-expansion emits the nested shape
    /// (<c>Where(Where(root, fkPred), userPred)</c>), whose user predicates the caller peels off as separate
    /// <c>Where</c> layers before this method ever sees a folded predicate — so no real query reaches the
    /// folded branch today.
    /// <para>
    /// EF-355. A conjunctive predicate is NEVER handed to <see cref="NativeCorrelationMatcher"/> whole: the
    /// matcher accepts <c>(x != null) AndAlso equality</c> as a null-guarded correlation without checking that
    /// the guarded member is the equality's own key, so a folded USER conjunct shaped <c>innerField != null</c>
    /// would be absorbed into the match and the bind would succeed with <paramref name="userBody"/>
    /// <see langword="null"/> — silently returning every child instead of only the non-null ones. Instead every
    /// top-level conjunct is classified here: exactly one must be the FK equality on its own; a conjunct that
    /// is a null-guard on that SAME key (the shape EF emits when the outer key's CLR type is nullable) is the
    /// correlation's own guard and is dropped, exactly as the matcher would have done; every OTHER conjunct is
    /// a user filter and is returned in <paramref name="userBody"/> for translation (a translation failure
    /// there declines the whole bind, so a user conjunct is never dropped). The distinguishing signal is key
    /// identity, not shape — the two are otherwise structurally identical.
    /// </para>
    /// </remarks>
    private static bool TrySplitCorrelation(
        Expression predicateBody, IEntityType outerEntityType, ParameterExpression outerParam,
        IEntityType targetEntityType, out INavigation navigation, out Expression? userBody)
    {
        userBody = null;

        // Non-conjunctive: the whole innermost predicate IS the FK correlation (or nothing recognizable).
        if (predicateBody.RemoveConvert() is not BinaryExpression { NodeType: ExpressionType.AndAlso })
            return NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                predicateBody, outerEntityType, outerParam, targetEntityType, requireEmbedded: false, out navigation);

        // Conjunctive: flatten top-level AndAlso conjuncts, find the ONE that is the FK correlation on its own,
        // then classify the rest (see the remarks above — this is where EF-355's silent drop was).
        navigation = null!;
        var conjuncts = new List<Expression>();
        FlattenAndAlso(predicateBody.RemoveConvert()!, conjuncts);

        Expression? fkConjunct = null;
        var rest = new List<Expression>();
        foreach (var conjunct in conjuncts)
        {
            if (fkConjunct == null
                && NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                    conjunct, outerEntityType, outerParam, targetEntityType, requireEmbedded: false, out navigation))
                fkConjunct = conjunct;
            else
                rest.Add(conjunct);
        }

        if (fkConjunct == null)
            return false;

        var userConjuncts = rest.Where(c => !IsCorrelationNullGuard(c, fkConjunct, outerParam)).ToList();
        if (userConjuncts.Count > 0)
            userBody = userConjuncts.Aggregate(Expression.AndAlso);
        return true;
    }

    /// <summary>
    /// Decides whether <paramref name="conjunct"/> is the FK correlation's OWN null-guard — <c>k != null</c>
    /// where <c>k</c> is the very same outer-key member <paramref name="fkConjunct"/> compares — as opposed to
    /// a user <c>!= null</c> filter on some other (inner-element) member. Only the former may be dropped; see
    /// <see cref="TrySplitCorrelation"/>'s remarks (EF-355).
    /// </summary>
    private static bool IsCorrelationNullGuard(Expression conjunct, Expression fkConjunct, ParameterExpression outerParam)
    {
        if (conjunct.RemoveConvert() is not BinaryExpression { NodeType: ExpressionType.NotEqual } notEqual)
            return false;

        Expression guarded;
        if (IsNullConstant(notEqual.Right)) guarded = notEqual.Left;
        else if (IsNullConstant(notEqual.Left)) guarded = notEqual.Right;
        else return false;

        // A guard on anything not rooted at the OUTER parameter is by definition an inner-element user filter.
        if (!ReferencesParameter(guarded, outerParam))
            return false;

        // ...and even an outer-rooted guard only counts when it guards the key the FK equality itself uses.
        return TryGetEqualitySides(fkConjunct, out var left, out var right)
               && (IsSameMemberAccess(guarded, left) || IsSameMemberAccess(guarded, right));
    }

    private static bool IsNullConstant(Expression node)
        => node.RemoveConvert() is ConstantExpression { Value: null };

    /// <summary>
    /// Extracts the two compared sides of the FK-equality conjunct, in the same three spellings
    /// <see cref="NativeCorrelationMatcher"/> accepts (<c>==</c>, <c>object.Equals(x, y)</c>, <c>x.Equals(y)</c>).
    /// Deliberately a local copy: the shared matcher's own contract is not widened for this caller.
    /// </summary>
    private static bool TryGetEqualitySides(Expression conjunct, out Expression left, out Expression right)
    {
        switch (conjunct.RemoveConvert())
        {
            case BinaryExpression { NodeType: ExpressionType.Equal } equal:
                left = equal.Left;
                right = equal.Right;
                return true;

            case MethodCallExpression
            {
                Method: { Name: nameof(Equals), IsStatic: true, DeclaringType: var declaringType },
                Arguments: [var arg0, var arg1]
            } when declaringType == typeof(object):
                left = arg0;
                right = arg1;
                return true;

            case MethodCallExpression { Method.Name: nameof(Equals), Object: { } instance, Arguments: [var arg] }:
                left = instance;
                right = arg;
                return true;

            default:
                left = right = null!;
                return false;
        }
    }

    /// <summary>
    /// Structural identity of two (possibly <c>EF.Property</c>-spelled, possibly <c>Convert</c>-wrapped) member
    /// accesses: same member name off the same root expression instance.
    /// </summary>
    private static bool IsSameMemberAccess(Expression a, Expression b)
        => a.RemoveConvert() is { } strippedA
           && b.RemoveConvert() is { } strippedB
           && TryGetMemberAccess(strippedA, out var rootA, out var nameA)
           && TryGetMemberAccess(strippedB, out var rootB, out var nameB)
           && nameA == nameB
           && ReferenceEquals(rootA.RemoveConvert(), rootB.RemoveConvert());

    private static void FlattenAndAlso(Expression expression, List<Expression> conjuncts)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
        {
            FlattenAndAlso(andAlso.Left, conjuncts);
            FlattenAndAlso(andAlso.Right, conjuncts);
        }
        else
        {
            conjuncts.Add(expression);
        }
    }

    /// <summary>
    /// Scans <paramref name="expression"/> for any reference to <paramref name="parameter"/> — used by
    /// <see cref="TryTranslateReferenceFilterLayer"/> to detect a user filter correlated beyond the FK equality
    /// already isolated by <see cref="TrySplitCorrelation"/>, and route it to the two-scope translator instead
    /// of the single-scope one (which resolves member access by name, not parameter identity, and would
    /// otherwise silently mistranslate an outer-scoped member sharing a name with the inner entity).
    /// </summary>
    private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
    {
        var visitor = new ParameterReferenceVisitor(parameter);
        visitor.Visit(expression);
        return visitor.Found;
    }

    /// <summary>
    /// Translates one peeled reference-<c>SelectMany</c> inner-filter <c>Where</c> layer into a filter conjunct.
    /// A layer referencing the outer <c>SelectMany</c> parameter (correlated beyond the FK) is translated with
    /// the two-scope translator — inner field refs prefixed with <paramref name="scope"/>, outer field refs at
    /// document root, routed by parameter identity — and renders as <c>$expr</c>. A layer referencing only the
    /// inner element keeps the single-scope translate + blanket-prefix path. Returns <see langword="false"/>
    /// (no mutation) when the layer cannot be translated.
    /// </summary>
    private static bool TryTranslateReferenceFilterLayer(
        Expression body, MongoExpressionTranslator innerTranslator, IEntityType innerEntityType, string scope,
        ParameterExpression outerParam, IEntityType outerEntityType, [NotNullWhen(true)] out MongoExpression? conjunct)
    {
        conjunct = null;

        if (ReferencesParameter(body, outerParam))
        {
            var twoScope = new MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, scope);
            if (!twoScope.TryTranslate(body, out var correlated))
                return false;
            conjunct = correlated; // already correctly scoped — do NOT blanket-prefix
            return true;
        }

        if (!innerTranslator.TryTranslate(body, out var innerExpr))
            return false;
        conjunct = MongoFieldPrefixRewriter.Rewrite(innerExpr, scope);
        return true;
    }

    private sealed class ParameterReferenceVisitor(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, parameter))
                Found = true;
            return base.VisitParameter(node);
        }
    }

    /// <summary>
    /// Binds the DEFERRED explicit-result-selector / query-syntax form of an owned-collection
    /// <c>SelectMany</c> — <c>SelectMany(o =&gt; o.Items, (o, i) =&gt; new {...})</c> and its query-syntax
    /// equivalent.
    /// </summary>
    /// <remarks>
    /// EF's nav-expansion normalizes both to the bare-nav collection-selector form (accepted elsewhere by the
    /// bare-nav path) wrapped in a <c>TransparentIdentifier(Outer, Inner)</c> result selector; the real
    /// projection is a separate trailing <c>Select(ti =&gt; new {ti.Outer.X, ti.Inner.Y})</c> over that
    /// transparent identifier — this method binds that trailing Select, given a query whose
    /// <see cref="MongoSelectDefinition.UnwindSource"/> is already set and whose
    /// <see cref="MongoSelectDefinition.Projection"/> is still empty.
    /// <para>
    /// Each projection leaf is a nested member access on the single <c>ti</c> parameter —
    /// <c>MemberExpression(MemberExpression(ti, "Outer"|"Inner"), &lt;member&gt;)</c> — not pre-folded by EF.
    /// Because <see cref="MongoExpressionTranslator.TryTranslateField"/> only resolves a
    /// <see cref="MemberExpression"/> whose own <c>Expression</c> is a bare <see cref="ParameterExpression"/>
    /// (it rejects <c>ti.Outer.X</c> outright), each leaf's member is re-rooted onto a synthetic parameter of
    /// the scope's own entity CLR type before translation — the same two structurally-separate translators
    /// (outer vs. inner) <see cref="TryBind"/> already uses, just fed a re-rooted expression.
    /// </para>
    /// </remarks>
    internal static bool TryBindTransparentIdentifierProjection(
        MongoQueryExpression mongoQ, LambdaExpression selector, out string? bareLeafAlias)
    {
        bareLeafAlias = null;

        var sources = mongoQ.Select.UnwindSources;
        if (sources.Count == 0 || mongoQ.Select.Projection.Count > 0)
            return false;
        if (selector.Parameters.Count != 1)
            return false;
        var ti = selector.Parameters[0];

        var isBareBody = !TryReadProjection(selector.Body, out var members);
        if (isBareBody)
        {
            // Deliberately narrow: ONLY an arithmetic computed body is admitted bare. A bare member access
            // (`ti.Inner.Name`, `ti.Outer.Name`) is a path-addressable leaf, whose alias would have to be its
            // own document path for the late-fallback read to stay correct (NativeProjectionBinder's tier 1) —
            // a different contract from the `_v` tier below, and outside this binder's scope; it keeps
            // declining (and falling back) exactly as before. `ti.Inner` (the whole element) is handled by the
            // caller's own WholeElement branch, not here.
            if (!IsArithmeticComputedLeaf(selector.Body))
                return false;

            // A BARE (non-`new {}`/`MemberInit`) computed body — what EF's nav-expansion folds the ONE-arg
            // `SelectMany(o => o.Items).Select(i => i.Price * 2)` shape into (`ti => ti.Inner.Price * 2`).
            // It carries no member name, so it is projected under the reserved `_v` alias, exactly the tier-2
            // (ProjectionAliasTier.Synthetic) convention NativeProjectionBinder uses for a bare computed body:
            // `_v` is what the driver itself names a bare projection, so a LATE fallback (which leaves this
            // query's captured chain un-stripped — ShouldStripBareProjectionOnFallback keys off a
            // DocumentPath override, and this path registers none) has the driver's own push-down write the
            // very element the alias-addressed shaper already reads by.
            members = [(NativeProjectionBinder.SyntheticBareProjectionAlias, selector.Body)];
        }

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        // One translator (+ synthetic re-rooting parameter) per scope: index 0 = the query root/owner, index
        // k (1..sources.Count) = UnwindSources[k-1] (the k-th SelectMany level's own unwound element).
        var translators = new MongoExpressionTranslator[sources.Count + 1];
        var scopeParams = new ParameterExpression[sources.Count + 1];
        translators[0] = new MongoExpressionTranslator(outerEntityType);
        scopeParams[0] = Expression.Parameter(outerEntityType.ClrType, "s0");
        for (var i = 0; i < sources.Count; i++)
        {
            translators[i + 1] = new MongoExpressionTranslator(sources[i].InnerEntityType);
            scopeParams[i + 1] = Expression.Parameter(sources[i].InnerEntityType.ClrType, "s" + (i + 1));
        }

        var projections = new List<MongoProjection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, argExpr) in members)
        {
            MongoExpression projected;

            if (argExpr is MemberExpression member
                && TryResolveScopeDepth(member.Expression, ti, sources.Count, out var scopeIndex))
            {
                var rerooted = Expression.MakeMemberAccess(scopeParams[scopeIndex], member.Member);
                if (!translators[scopeIndex].TryTranslateField(rerooted, out var field))
                    return false;

                projected = scopeIndex > 0
                    ? new MongoFieldExpression(field.Property, sources[scopeIndex - 1].InnerScopePath + "." + field.ElementName)
                    : field;
            }
            else if (IsArithmeticComputedLeaf(argExpr)
                     && TryTranslateComputedLeaf(argExpr, ti, sources, translators, scopeParams, out var computed))
            {
                projected = computed;
            }
            else
            {
                return false;
            }

            // A bare body's `_v` leaf mirrors NativeProjectionBinder's tier-2 arm 1b, whose boundary is a
            // SUBTREE fact, not a top-node one: a `$size` anywhere under it renders — on the un-stripped
            // driver fallback — as a bare `$size`, which is a hard server error (not a wrong answer) against a
            // missing or explicitly-null array. Asked through the binder's own predicate rather than restated
            // here, so the two can't drift. Only the bare tier is held to it: a wrapped (named-alias) leaf is
            // read back by its own member name on either route and is unchanged by this slice.
            if (isBareBody && !NativeProjectionBinder.IsArrayFreeComputedSubtree(projected))
                return false;

            if (!seen.Add(alias)) return false;
            projections.Add(new MongoProjection(alias, projected));
        }

        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        if (isBareBody)
            bareLeafAlias = members[0].Alias;
        return true;
    }

    /// <summary>
    /// The arithmetic node shapes a computed projection leaf may take — the gate both the wrapped
    /// (named-alias) member loop and the bare-body arm call, so the two can never admit different shapes.
    /// </summary>
    private static bool IsArithmeticComputedLeaf(Expression expression)
        => expression is BinaryExpression
        {
            NodeType: ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
            or ExpressionType.Divide or ExpressionType.Modulo
        };

    /// <summary>
    /// Translates an arithmetic computed projection leaf, trying the SINGLE-scope form first
    /// (<see cref="TryTranslateSingleScopeComputedLeaf"/> — every scope-rooted operand resolving to one scope,
    /// which re-roots the whole subtree at once) and falling back to the CROSS-scope form
    /// (<see cref="TryTranslateCrossScopeComputedLeaf"/>) only for a leaf the former declines.
    /// </summary>
    /// <remarks>
    /// The ordering is deliberate and not merely an optimization: the single-scope path re-roots and
    /// translates the leaf as ONE subtree, so a wholly-inner leaf is prefixed once, at the top, exactly as it
    /// was before this fallback existed — every previously-native leaf keeps its previous translation
    /// byte-for-byte, and the cross-scope path is reached only by leaves that used to decline.
    /// </remarks>
    private static bool TryTranslateComputedLeaf(
        Expression leaf,
        ParameterExpression ti,
        IReadOnlyList<MongoUnwindSource> sources,
        MongoExpressionTranslator[] translators,
        ParameterExpression[] scopeParams,
        [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateSingleScopeComputedLeaf(leaf, ti, sources, translators, scopeParams, out result)
           || TryTranslateCrossScopeComputedLeaf(leaf, ti, sources, translators, scopeParams, out result);

    /// <summary>
    /// Translates a SINGLE-SCOPE arithmetic computed projection leaf — every scope-rooted member operand
    /// (<c>ti.Outer…</c>/<c>ti.Inner…</c>) in the leaf must resolve to the same scope. Re-roots the whole
    /// arithmetic subtree onto that scope's synthetic parameter, reuses
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/>, then prefixes inner-scope field refs with the
    /// unwind path via <see cref="MongoFieldPrefixRewriter"/>. Declines (returns <see langword="false"/>, no
    /// mutation) for a cross-scope leaf (e.g. <c>o.Discount * i.Price</c> — handled by
    /// <see cref="TryTranslateCrossScopeComputedLeaf"/>, which callers reach via
    /// <see cref="TryTranslateComputedLeaf"/>), a leaf with no scope-rooted operand, or anything
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> rejects.
    /// </summary>
    private static bool TryTranslateSingleScopeComputedLeaf(
        Expression leaf,
        ParameterExpression ti,
        IReadOnlyList<MongoUnwindSource> sources,
        MongoExpressionTranslator[] translators,
        ParameterExpression[] scopeParams,
        [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateScopedSubtree(
            leaf, ti, sources, translators, scopeParams, requireScopeRooted: true, out result);

    /// <summary>
    /// Translates an arithmetic computed projection leaf whose operands span TWO scopes — e.g.
    /// <c>ti.Outer.Discount * ti.Inner.Price</c>, which <see cref="TryTranslateSingleScopeComputedLeaf"/>
    /// declines because its re-rooting visitor flags <see cref="ScopeRerootingVisitor.CrossScope"/>.
    /// </summary>
    /// <remarks>
    /// Instead of re-rooting the WHOLE subtree onto one synthetic parameter, each operand is translated
    /// independently, against whichever single scope IT is rooted on, and prefixed with that scope's own
    /// unwind path before the two are recombined under the leaf's own arithmetic operator (mapped by
    /// <see cref="MongoExpressionTranslator.MapArithmeticOperator"/> — the same mapper the single-scope path
    /// reaches through <see cref="MongoExpressionTranslator.TryTranslateValue"/>, so the two agree on the
    /// EF-434 integral-division split for free). An operand that is itself cross-scope recurses
    /// (<see cref="TryTranslateScopedOperand"/>), so <c>(o.Rank * i.Price) + 1</c> binds too.
    /// <para>
    /// The recombined node is a field-to-field arithmetic expression, which has NO query-dialect form. That
    /// costs nothing here: a <c>$project</c> body renders every leaf through
    /// <see cref="MongoAggregationExpressionRenderer"/> unconditionally
    /// (<c>MongoPipelineFactory.RenderProject</c>) — there is no dialect choice to make, and no <c>$expr</c>
    /// wrapper either. Only a <c>$match</c> predicate has the two-dialect split, and this method is reachable
    /// only from the projection binder.
    /// </para>
    /// </remarks>
    private static bool TryTranslateCrossScopeComputedLeaf(
        Expression leaf,
        ParameterExpression ti,
        IReadOnlyList<MongoUnwindSource> sources,
        MongoExpressionTranslator[] translators,
        ParameterExpression[] scopeParams,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (leaf is not BinaryExpression binary
            || MongoExpressionTranslator.MapArithmeticOperator(binary) is not { } op)
            return false;

        // Mirrors TranslateOperand's own arithmetic guard, which this method bypasses by recombining the two
        // operands itself: ExpressionType.Add over strings is compiler-generated concatenation, NOT arithmetic
        // — the server rejects "$add" on strings outright. Without this, `ti.Outer.Name + ti.Inner.Name` (a
        // perfectly cross-scope Add) would be recombined into a `$add` of two string fields.
        if (!MongoExpressionTranslator.IsNumericType(binary.Type))
            return false;

        // Reachable ONLY for a leaf the single-scope path declined FOR THE SCOPE REASON. This is the whole
        // admission boundary of this method and it is deliberately expressed as the very signal the
        // single-scope path declines on (ScopeRerootingVisitor.CrossScope over the same subtree), not as a
        // restatement of "what looks cross-scope". Both regressions this guard prevents were measured: without
        // it, a leaf with NO scope-rooted operand at all (`2m * 3m`) binds through the scope-free operand arm,
        // and a leaf the value translator rejected for a non-scope reason (string concat, before the numeric
        // guard above) gets a second, weaker chance at translation here.
        var scopes = new ScopeRerootingVisitor(ti, sources.Count, scopeParams);
        scopes.Visit(leaf);
        if (!scopes.CrossScope)
            return false;

        if (!TryTranslateScopedOperand(binary.Left, ti, sources, translators, scopeParams, out var left)
            || !TryTranslateScopedOperand(binary.Right, ti, sources, translators, scopeParams, out var right))
            return false;

        result = new MongoBinaryExpression(op, left, right);
        return true;
    }

    /// <summary>
    /// Translates ONE operand of a cross-scope arithmetic leaf: a wholly single-scope (or wholly
    /// scope-free — a constant/query parameter) operand via the shared re-root-and-prefix path, otherwise a
    /// nested cross-scope arithmetic operand by recursion.
    /// </summary>
    private static bool TryTranslateScopedOperand(
        Expression operand,
        ParameterExpression ti,
        IReadOnlyList<MongoUnwindSource> sources,
        MongoExpressionTranslator[] translators,
        ParameterExpression[] scopeParams,
        [NotNullWhen(true)] out MongoExpression? result)
        => TryTranslateScopedSubtree(
               operand, ti, sources, translators, scopeParams, requireScopeRooted: false, out result)
           || TryTranslateCrossScopeComputedLeaf(operand, ti, sources, translators, scopeParams, out result);

    /// <summary>
    /// The shared re-root / translate / prefix core, factored out of the original single-scope computed-leaf
    /// method so the cross-scope path reuses it per operand rather than duplicating the
    /// <see cref="ScopeRerootingVisitor"/> construction.
    /// </summary>
    /// <remarks>
    /// <paramref name="requireScopeRooted"/> is the ONLY difference between the two callers, and it preserves
    /// the original method's behaviour exactly: a whole LEAF containing no scope-rooted member at all
    /// (<c>2m * 3m</c>) still declines, because admitting it would push a constant-only <c>$project</c> leaf
    /// down for a shape the binder never claimed. An OPERAND of an otherwise cross-scope leaf may legitimately
    /// be scope-free (the <c>1</c> in <c>(o.Rank * i.Price) + 1</c>), and translating it against
    /// <c>translators[0]</c> is immaterial — with no scope-rooted member, the translation contains no field
    /// reference for a translator or a prefix to disagree about.
    /// </remarks>
    private static bool TryTranslateScopedSubtree(
        Expression subtree,
        ParameterExpression ti,
        IReadOnlyList<MongoUnwindSource> sources,
        MongoExpressionTranslator[] translators,
        ParameterExpression[] scopeParams,
        bool requireScopeRooted,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        var visitor = new ScopeRerootingVisitor(ti, sources.Count, scopeParams);
        var rerooted = visitor.Visit(subtree);
        if (visitor.CrossScope)
            return false;

        if (visitor.ResolvedScope is not { } scope)
        {
            if (requireScopeRooted)
                return false;
            scope = 0;
        }

        if (!translators[scope].TryTranslateValue(rerooted, out var computed))
            return false;

        result = scope > 0
            ? MongoFieldPrefixRewriter.Rewrite(computed, sources[scope - 1].InnerScopePath)
            : computed;
        return true;
    }

    /// <summary>
    /// Rewrites every scope-rooted transparent-identifier member access (<c>ti.Outer…/ti.Inner…</c>) in an
    /// arithmetic leaf onto the matching per-scope synthetic parameter, recording the single scope it resolves
    /// to (or flagging <see cref="CrossScope"/> if operands span more than one). A non-scope-rooted member is
    /// left untouched, so a constant/parameter operand still reaches
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> unchanged.
    /// </summary>
    private sealed class ScopeRerootingVisitor(ParameterExpression ti, int sourceCount, ParameterExpression[] scopeParams)
        : ExpressionVisitor
    {
        public int? ResolvedScope { get; private set; }
        public bool CrossScope { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (TryResolveScopeDepth(node.Expression, ti, sourceCount, out var scope))
            {
                if (ResolvedScope is { } prior && prior != scope)
                    CrossScope = true;
                ResolvedScope = scope;
                return Expression.MakeMemberAccess(scopeParams[scope], node.Member);
            }

            return base.VisitMember(node);
        }
    }

    /// <summary>
    /// Peels a chain of <c>ti.Outer</c>/<c>ti.Outer.Outer</c>/…/<c>ti.Inner</c> member accesses down to the
    /// bare <paramref name="ti"/> parameter, and resolves which scope it refers to (generalizes the 2-scope
    /// <c>ti.Outer</c>/<c>ti.Inner</c> shape to N scopes). Given <paramref name="sourceCount"/> chained unwind
    /// sources, the <c>k</c>-th level's own element is reached via <c>(sourceCount - k)</c> leading
    /// <c>"Outer"</c> hops followed by exactly one trailing <c>"Inner"</c> hop; the query root (owner) is
    /// reached via exactly <paramref name="sourceCount"/> <c>"Outer"</c> hops and no <c>"Inner"</c> at all.
    /// <paramref name="scopeIndex"/> is <c>0</c> for the root, or <c>k</c> (1-based) for
    /// <c>UnwindSources[k-1]</c>. Returns <see langword="false"/> — declining cleanly — for any chain that
    /// does not terminate exactly at <paramref name="ti"/>, is empty, exceeds <paramref name="sourceCount"/>
    /// hops, or does not match either of the two valid shapes above.
    /// </summary>
    private static bool TryResolveScopeDepth(Expression? scopeAccess, ParameterExpression ti, int sourceCount, out int scopeIndex)
    {
        scopeIndex = -1;
        var path = new List<string>();
        var current = scopeAccess;
        while (current is MemberExpression { Member.Name: "Outer" or "Inner" } hop)
        {
            path.Add(hop.Member.Name);
            current = hop.Expression;
        }

        if (current != ti || path.Count == 0 || path.Count > sourceCount)
            return false;

        path.Reverse(); // now ordered outward-from-ti: path[0] is the first hop off ti.

        if (path[^1] == "Inner" && path.Take(path.Count - 1).All(h => h == "Outer"))
        {
            scopeIndex = sourceCount - path.Count + 1;
            return true;
        }

        if (path.Count == sourceCount && path.All(h => h == "Outer"))
        {
            scopeIndex = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Translates a single already-scope-rooted member access (its <c>Expression</c> is a bare
    /// <see cref="ParameterExpression"/> of the target scope's own CLR type) via whichever of
    /// <paramref name="outerTranslator"/>/<paramref name="innerTranslator"/> matches <paramref name="isInner"/>,
    /// prefixing an inner match's element name with <paramref name="unwindPath"/>. The one piece of logic both
    /// <see cref="TryBind"/> and <see cref="TryBindTransparentIdentifierProjection"/> share once each has
    /// resolved which scope a leaf belongs to by its own means.
    /// </summary>
    private static bool TryTranslateScopedField(
        MongoExpressionTranslator outerTranslator, MongoExpressionTranslator innerTranslator,
        string unwindPath, Expression memberAccess, bool isInner, out MongoFieldExpression field)
    {
        if (!isInner)
        {
            if (!outerTranslator.TryTranslateField(memberAccess, out var outerField))
            {
                field = null!;
                return false;
            }

            field = outerField;
            return true;
        }

        if (!innerTranslator.TryTranslateField(memberAccess, out var innerField))
        {
            field = null!;
            return false;
        }

        field = new MongoFieldExpression(innerField.Property, unwindPath + "." + innerField.ElementName);
        return true;
    }

    /// <summary>
    /// Peels user-authored <c>Where(...)</c> layers off an owned collection selector's source down to the bare
    /// owned-nav member access, collecting each layer's predicate lambda into <paramref name="userPredicates"/>.
    /// Owned collections nav-expand to a bare member access (<c>o.Items</c>), not an FK-correlated subquery, so
    /// every <c>Where</c> here is an inner-element user filter (unlike <see cref="TryBindReferenceNavUnwind"/>,
    /// there is no FK-correlation <c>Where</c> to stop at). Returns the source with all <c>Where</c> layers
    /// removed; the caller validates it via <see cref="TryGetMemberAccess"/>.
    /// </summary>
    private static Expression PeelOwnedInnerWhere(Expression source, List<LambdaExpression> userPredicates)
    {
        var current = UnwrapAsQueryable(source);
        while (current is MethodCallExpression
               {
                   Method: { Name: nameof(System.Linq.Queryable.Where), DeclaringType: var decl },
                   Arguments: [var whereSource, var predArg]
               }
               && decl == typeof(System.Linq.Queryable))
        {
            userPredicates.Add(predArg.UnwrapLambdaFromQuote());
            current = UnwrapAsQueryable(whereSource);
        }
        return current;
    }

    /// <summary>
    /// Translates each peeled owned inner-element predicate and ANDs them into one <paramref name="filter"/>.
    /// An inner-only layer is translated against <paramref name="innerEntityType"/> and its field refs are
    /// prefixed with <paramref name="unwindPath"/> (e.g. <c>Price</c> becomes <c>Items.Price</c>, matching where
    /// the unwound owned element sits before <c>$replaceRoot</c>/<c>$project</c>). A layer referencing the
    /// outer parameter (e.g. <c>i.Name == o.Name</c>) is instead routed to the two-scope
    /// <see cref="MongoExpressionTranslator"/> — routing is by parameter identity (see
    /// <see cref="ReferencesParameter"/>), never by member name, so a name shared between the outer and inner
    /// entity types never mis-scopes; the result renders as <c>$expr</c>. Returns <see langword="true"/> with
    /// <paramref name="filter"/> <see langword="null"/> when there are no predicates, so callers can invoke it
    /// unconditionally. Declines (<see langword="false"/>, no mutation) only if a translator rejects the layer
    /// — a correlated owned <c>SelectMany</c> has no driver-LINQ oracle, so a decline hard-fails in every mode.
    /// </summary>
    private static bool TryBuildOwnedInnerFilter(
        IReadOnlyList<LambdaExpression> userPredicates, IEntityType innerEntityType, string unwindPath,
        ParameterExpression outerParam, IEntityType outerEntityType, out MongoExpression? filter)
    {
        filter = null;
        if (userPredicates.Count == 0)
            return true;

        var innerTranslator = new MongoExpressionTranslator(innerEntityType);
        foreach (var userPredicate in userPredicates)
        {
            if (userPredicate.Parameters.Count != 1)
                return false;

            MongoExpression conjunct;
            if (ReferencesParameter(userPredicate.Body, outerParam))
            {
                // Correlated-beyond-outer: translate with the two-scope translator (inner fields prefixed with
                // the unwind path, outer fields at document root, routed by parameter identity), used directly
                // — not blanket-prefixed. Renders as $expr. Declines cleanly (no mutation) if unsupported; a
                // correlated owned SelectMany has no driver-LINQ oracle, so a decline hard-fails every mode.
                var twoScope = new MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, unwindPath);
                if (!twoScope.TryTranslate(userPredicate.Body, out var correlated))
                    return false;
                conjunct = correlated;
            }
            else
            {
                if (!innerTranslator.TryTranslate(userPredicate.Body, out var expr))
                    return false;
                conjunct = MongoFieldPrefixRewriter.Rewrite(expr!, unwindPath);
            }

            filter = filter == null
                ? conjunct
                : new MongoBinaryExpression(MongoBinaryOperator.AndAlso, filter, conjunct);
        }
        return true;
    }

    private static Expression UnwrapAsQueryable(Expression e)
        => e is MethodCallExpression { Method.Name: nameof(System.Linq.Queryable.AsQueryable), Arguments: [var inner] }
            ? inner : e;

    /// <summary>
    /// Matches a member/navigation access in either of the two shapes EF Core produces: a plain
    /// <see cref="MemberExpression"/> (ordinary scalar property access) or an <c>EF.Property(root, "Name")</c>
    /// call (the shadow-nav-safe form EF's nav-expansion rewrites navigation access into, e.g. <c>o.Items</c>
    /// becoming <c>EF.Property(o, "Items")</c>). Returns the accessed root expression and member name.
    /// </summary>
    private static bool TryGetMemberAccess(Expression expression, out Expression root, out string name)
    {
        switch (expression)
        {
            case MemberExpression { Expression: { } inner } member:
                root = inner;
                name = member.Member.Name;
                return true;

            case MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [var rootArg, ConstantExpression { Value: string propName }]:
                root = rootArg;
                name = propName;
                return true;

            default:
                root = null!;
                name = null!;
                return false;
        }
    }

    // new {...} (NewExpression with Members) or a parameterless MemberInit — mirrors NativeProjectionBinder.
    private static bool TryReadProjection(Expression body, out IReadOnlyList<(string Alias, Expression Arg)> members)
    {
        members = null!;
        var list = new List<(string, Expression)>();
        switch (body)
        {
            case NewExpression ne when ne.Members != null && ne.Members.Count == ne.Arguments.Count && ne.Arguments.Count > 0:
                for (var i = 0; i < ne.Arguments.Count; i++) list.Add((ne.Members[i].Name, ne.Arguments[i]));
                break;
            case MemberInitExpression mi when mi.NewExpression.Arguments.Count == 0 && mi.Bindings.Count > 0:
                foreach (var b in mi.Bindings)
                {
                    if (b is not MemberAssignment ma) return false;
                    list.Add((b.Member.Name, ma.Expression));
                }
                break;
            default:
                return false;
        }
        members = list;
        return true;
    }
}
