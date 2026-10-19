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
/// <see cref="TryBind"/> handles the inner-<c>Select</c> form
/// (<c>o =&gt; o.Items.AsQueryable().Select(i =&gt; new { o.X, i.Y })</c>). The result-selector / query-syntax
/// form (<c>SelectMany(o =&gt; o.Items, (o, i) =&gt; ...)</c>) normalizes to a bare nav selector plus a separate
/// trailing <c>Select</c> over <c>TransparentIdentifier(Outer, Inner)</c>: <see cref="TryBindBareNavUnwind"/>
/// sets the unwind source and <see cref="TryBindTransparentIdentifierProjection"/> binds the projection.
/// Outer and inner members resolve through two separate translators by parameter identity (see
/// <c>Query/AGENTS.md</c>). A <see langword="false"/> return leaves the select untouched; for
/// <see cref="TryBind"/>/<see cref="TryBindBareNavUnwind"/> the caller then returns <see langword="null"/> and
/// EF hard-fails translation.
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

        // EF's nav-expansion rewrites nav access to EF.Property(o, "Nav"), so accept both forms. Every peeled Where is
        // an inner-element user filter (owned collections have no FK correlation).
        var userPredicates = new List<LambdaExpression>();
        var navExpr = PeelOwnedInnerWhere(selectSource, userPredicates);
        if (!navExpr.TryGetMemberOrEFProperty(out var navRoot, out var navName) || !ReferenceEquals(navRoot, outerParam))
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

        if (!innerLambda.Body.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true))
            return false;

        var outerTranslator = new MongoExpressionTranslator(outerEntityType);
        var innerTranslator = new MongoExpressionTranslator(navigation.TargetEntityType);
        var projections = new List<MongoProjection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, argExpr) in members)
        {
            if (!argExpr.TryGetMemberOrEFProperty(out var root, out _))
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
    /// Binds the bare-nav collection selector (<c>o =&gt; o.Items.AsQueryable()</c>, no nested <c>Select</c>) that
    /// nav-expansion produces for the result-selector and query-syntax forms of an owned-collection
    /// <c>SelectMany</c>.
    /// </summary>
    /// <remarks>
    /// Sets only <see cref="MongoSelectDefinition.UnwindSource"/>; the projection is bound later by
    /// <see cref="TryBindTransparentIdentifierProjection"/>.
    /// </remarks>
    internal static bool TryBindBareNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        var userPredicates = new List<LambdaExpression>();
        var navExpr = PeelOwnedInnerWhere(collectionSelector.Body, userPredicates);
        if (!navExpr.TryGetMemberOrEFProperty(out var navRoot, out var navName) || !ReferenceEquals(navRoot, outerParam))
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
    /// Binds a cross-collection reference-nav <c>SelectMany</c>
    /// (<c>from c in q from o in c.Orders select new {...}</c>).
    /// </summary>
    /// <remarks>
    /// Nav-expansion turns the selector into a correlated subquery,
    /// <c>Where(EntityQueryRootExpression&lt;Target&gt;, o =&gt; c.pk == o.fk)</c>, matched via
    /// <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/> with <c>requireEmbedded: false</c> so it
    /// partitions the shape space with <see cref="TryBindBareNavUnwind"/>. Registers a <c>ForceUnwind</c>
    /// <c>$lookup</c> and a <see cref="MongoUnwindSourceKind.Reference"/> source scoped at <c>_lookup_&lt;Nav&gt;</c>;
    /// the projection is bound later by <see cref="TryBindTransparentIdentifierProjection"/>.
    /// </remarks>
    internal static bool TryBindReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        // c.Refs.Where(p1).Where(p2) nav-expands to Where(Where(Where(root, fkPred), p1), p2): the innermost Where
        // carries EF's FK correlation, every outer one is a user filter. A folded fkPred && userPred is split below.
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

        // Separate the FK correlation from any folded user conjunct; the shared matcher still rejects extra conjuncts.
        if (!TrySplitCorrelation(predicate.Body, outerEntityType, outerParam, root.EntityType,
                out var navigation, out var foldedUserBody))
            return false;

        // AND all user filters into one predicate. Inner-only layers translate against the target prefixed with the
        // $lookup scope; layers also referencing outer members use the two-scope translator. No partial mutation.
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

        // A reference SelectMany flatten is always inner-join (childless principals drop out), overriding
        // LookupExpression's Include-oriented default.
        var lookup = new LookupExpression(navigation, forceUnwind: true) { PreserveNullAndEmptyArrays = false };
        // AddLookup dedupes on alias, so a pending same-nav lookup would leave UnwindSource.Lookup pointing at an
        // instance not in the pending list. An Include lookup can't collide (reference SelectMany is projected-only),
        // but a reference-collection Count predicate over the same nav can; MongoSelectDefinition.Route declines that
        // combination to Fallback before lowering. See NativeReferenceCollectionCountPredicateTests
        // .Count_predicate_before_correlated_SelectMany_declines_cleanly_in_every_mode.
        mongoQ.AddLookup(lookup);
        var unwind = MongoUnwindSource.Reference(scope, navigation.TargetEntityType, lookup);
        unwind.Filter = filter;
        mongoQ.Select.AddUnwindSource(unwind);
        return true;
    }

    /// <summary>
    /// Binds the second level of a nested cross-collection reference <c>SelectMany</c>
    /// (<c>from o in q from m in o.Mids from l in m.Leaves select ...</c>).
    /// </summary>
    /// <remarks>
    /// Same correlated-subquery shape as <see cref="TryBindReferenceNavUnwind"/>, except the outer key is
    /// <c>ti.Inner.&lt;pk&gt;</c> off level 1's transparent identifier. <c>ti.Inner</c> is rewritten onto a
    /// synthetic level-1-entity parameter so <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/>
    /// can be reused unchanged.
    /// <para>
    /// Requires exactly one prior reference unwind source. The second <c>ForceUnwind</c> lookup's
    /// <see cref="LookupExpression.LocalField"/> is scoped under level 1's
    /// <see cref="MongoUnwindSource.InnerScopePath"/> (e.g. <c>_lookup_Mids._id</c>). No partial mutation on
    /// decline. Unfiltered only: a level-2 filter adds an outer <c>Where</c>, which doesn't match and declines.
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

        // Rewrite `ti.Inner` onto a level-1-entity parameter so the single-level matcher recognizes the correlation.
        var level1Param = Expression.Parameter(level1Source.InnerEntityType.ClrType, "l1");
        var rewritten = new TransparentIdentifierInnerRewriter(ti, level1Param).Visit(predicate.Body);

        if (!NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                rewritten, level1Source.InnerEntityType, level1Param, root.EntityType, requireEmbedded: false, out var navigation))
            return false;

        var scope2 = LookupExpression.GetLookupAlias(navigation);
        // Always inner-join, as at level 1.
        var lookup2 = new LookupExpression(navigation, forceUnwind: true) { PreserveNullAndEmptyArrays = false };
        lookup2.LocalField = level1Source.InnerScopePath + "." + lookup2.LocalField;
        mongoQ.AddLookup(lookup2);
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Reference(scope2, navigation.TargetEntityType, lookup2));
        return true;
    }

    /// <summary>
    /// Rewrites <c>tiParam.Inner</c> onto <paramref name="replacement"/>, turning <c>ti.Inner.&lt;pk&gt;</c> into a
    /// bare-parameter-rooted access <see cref="NativeCorrelationMatcher"/> recognizes.
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
    /// Resolves the FK-correlated reference navigation from the innermost <c>Where</c> predicate, separating it
    /// from any user filter folded into the same predicate (<c>fkPred &amp;&amp; userPred</c>).
    /// </summary>
    /// <remarks>
    /// The folded branch is defensive: nav-expansion emits nested <c>Where</c>s, which the caller peels first.
    /// <para>
    /// A conjunction is never passed to <see cref="NativeCorrelationMatcher"/> whole: it accepts
    /// <c>(x != null) AndAlso equality</c> as a null-guarded correlation without checking the guarded member is the
    /// equality's key, so a user <c>innerField != null</c> conjunct would be silently absorbed (returning every
    /// child). Instead each conjunct is classified: exactly one is the FK equality, a null-guard on that same key is
    /// dropped, and every other conjunct is returned in <paramref name="userBody"/> (whose translation failure
    /// declines the bind). Key identity, not shape, is the distinguishing signal.
    /// </para>
    /// </remarks>
    private static bool TrySplitCorrelation(
        Expression predicateBody, IEntityType outerEntityType, ParameterExpression outerParam,
        IEntityType targetEntityType, out INavigation navigation, out Expression? userBody)
    {
        userBody = null;

        // Non-conjunctive: the whole predicate is the FK correlation (or nothing recognizable).
        if (predicateBody.RemoveConvert() is not BinaryExpression { NodeType: ExpressionType.AndAlso })
            return NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                predicateBody, outerEntityType, outerParam, targetEntityType, requireEmbedded: false, out navigation);

        // Conjunctive: find the one conjunct that is the FK correlation, then classify the rest (see remarks).
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
    /// Whether <paramref name="conjunct"/> is the FK correlation's own null-guard (<c>k != null</c> on the same
    /// outer-key member <paramref name="fkConjunct"/> compares), as opposed to a user <c>!= null</c> filter.
    /// </summary>
    private static bool IsCorrelationNullGuard(Expression conjunct, Expression fkConjunct, ParameterExpression outerParam)
    {
        if (conjunct.RemoveConvert() is not BinaryExpression { NodeType: ExpressionType.NotEqual } notEqual)
            return false;

        Expression guarded;
        if (NativeCorrelationMatcher.IsNullConstant(notEqual.Right)) guarded = notEqual.Left;
        else if (NativeCorrelationMatcher.IsNullConstant(notEqual.Left)) guarded = notEqual.Right;
        else return false;

        // A guard not rooted at the outer parameter is an inner-element user filter.
        if (!guarded.ReferencesParameter(outerParam))
            return false;

        // An outer-rooted guard counts only when it guards the FK equality's own key.
        return NativeCorrelationMatcher.TryExtractEqualitySides(fkConjunct, out var left, out var right)
               && (IsSameMemberAccess(guarded, left) || IsSameMemberAccess(guarded, right));
    }

    /// <summary>
    /// Structural identity of two (possibly <c>EF.Property</c>-spelled, possibly <c>Convert</c>-wrapped) member
    /// accesses: same member name off the same root expression instance.
    /// </summary>
    private static bool IsSameMemberAccess(Expression a, Expression b)
        => a.RemoveConvert() is { } strippedA
           && b.RemoveConvert() is { } strippedB
           && strippedA.TryGetMemberOrEFProperty(out var rootA, out var nameA)
           && strippedB.TryGetMemberOrEFProperty(out var rootB, out var nameB)
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
    /// Translates one peeled reference-<c>SelectMany</c> filter layer. A layer referencing the outer parameter
    /// uses the two-scope translator (inner refs prefixed with <paramref name="scope"/>, outer refs at root,
    /// rendered as <c>$expr</c>); an inner-only layer is translated then prefixed. No mutation on failure.
    /// </summary>
    private static bool TryTranslateReferenceFilterLayer(
        Expression body, MongoExpressionTranslator innerTranslator, IEntityType innerEntityType, string scope,
        ParameterExpression outerParam, IEntityType outerEntityType, [NotNullWhen(true)] out MongoExpression? conjunct)
    {
        conjunct = null;

        if (body.ReferencesParameter(outerParam))
        {
            var twoScope = new MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, scope);
            if (!twoScope.TryTranslate(body, out var correlated))
                return false;
            conjunct = correlated; // already scoped; don't prefix
            return true;
        }

        if (!innerTranslator.TryTranslate(body, out var innerExpr))
            return false;

        return MongoFieldPrefixRewriter.TryRewrite(innerExpr, scope, out conjunct);
    }


    /// <summary>
    /// Binds the trailing <c>Select(ti =&gt; new { ti.Outer.X, ti.Inner.Y })</c> of the result-selector /
    /// query-syntax form of <c>SelectMany</c>, given an already-set unwind source and an empty projection.
    /// </summary>
    /// <remarks>
    /// Each leaf is <c>ti.Outer.X</c>/<c>ti.Inner.Y</c>, which
    /// <see cref="MongoExpressionTranslator.TryTranslateField"/> rejects (it needs a bare-parameter root), so each
    /// member is re-rooted onto a synthetic parameter of its scope's entity type before translation.
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

        var isBareBody = !selector.Body.TryGetProjectionMembers(out var members, allowPositionalConstructorArguments: true);
        if (isBareBody)
        {
            // Only an arithmetic computed body is admitted bare. A bare member access would need its document path as
            // alias for the late-fallback read (NativeProjectionBinder's tier 1), so it declines; `ti.Inner` (whole
            // element) is handled by the caller's WholeElement branch.
            if (!IsArithmeticComputedLeaf(selector.Body))
                return false;

            // EF folds `SelectMany(o => o.Items).Select(i => i.Price * 2)` into `ti => ti.Inner.Price * 2`. With
            // no member name it takes the reserved `_v` alias (ProjectionAliasTier.Synthetic): that's what the
            // driver names a bare projection, so a late fallback (chain un-stripped) writes what the shaper reads.
            members = [(NativeProjectionBinder.SyntheticBareProjectionAlias, selector.Body)];
        }

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        // Index 0 = the query root; index k = UnwindSources[k-1] (the k-th SelectMany level's element).
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
                && MongoTransparentScopeResolver.TryResolveScopeDepth(
                    member.Expression, ti, hopNames: ["Outer", "Inner"], sources.Count, out var scopeIndex))
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

            // As in NativeProjectionBinder's tier-2 arm: a `$size` anywhere in a bare `_v` leaf renders as a bare
            // `$size` on the un-stripped fallback, a hard server error on a missing/null array. Wrapped leaves are
            // unaffected.
            if (isBareBody && !NativeProjectionBinder.IsArrayFreeComputedSubtree(projected))
                return false;

            if (!seen.Add(alias)) return false;
            projections.Add(new MongoProjection(alias, projected));
        }

        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        if (isBareBody)
            bareLeafAlias = members[0].MemberName;
        return true;
    }

    /// <summary>
    /// Arithmetic shapes a computed leaf may take; shared by the wrapped and bare arms so they admit the same set.
    /// </summary>
    private static bool IsArithmeticComputedLeaf(Expression expression)
        => expression is BinaryExpression
        {
            NodeType: ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
            or ExpressionType.Divide or ExpressionType.Modulo
        };

    /// <summary>
    /// Translates an arithmetic computed leaf, trying the single-scope form first and the cross-scope form only if
    /// it declines.
    /// </summary>
    /// <remarks>
    /// Order matters: the single-scope path translates the leaf as one subtree and prefixes once at the top, so
    /// wholly-single-scope leaves keep that translation exactly.
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
    /// Translates a computed leaf whose scope-rooted operands all resolve to one scope: re-roots the subtree onto
    /// that scope's parameter, uses <see cref="MongoExpressionTranslator.TryTranslateValue"/>, then prefixes inner
    /// field refs. Declines for cross-scope leaves, leaves with no scope-rooted operand, or untranslatable values.
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
    /// Translates an arithmetic leaf whose operands span scopes (e.g. <c>ti.Outer.Discount * ti.Inner.Price</c>).
    /// </summary>
    /// <remarks>
    /// Each operand is translated against its own scope and prefixed, then recombined under the operator from
    /// <see cref="MongoExpressionTranslator.MapArithmeticOperator"/> (the same mapper the single-scope path uses, so
    /// the integral-division handling agrees). Cross-scope operands recurse, so <c>(o.Rank * i.Price) + 1</c> binds.
    /// The result has no query-dialect form, which is fine: <c>$project</c> always renders via
    /// <see cref="MongoAggregationExpressionRenderer"/>.
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

        // String Add is concatenation; recombining it would emit a server-rejected `$add` of two string fields.
        if (!MongoExpressionTranslator.IsNumericType(binary.Type))
            return false;

        // Admit only leaves the single-scope path declined for the cross-scope reason. Otherwise a leaf with no
        // scope-rooted operand (`2m * 3m`), or one rejected for another reason, would get a second, weaker chance here.
        var scopes = new MongoTransparentScopeResolver.ScopeRerootingVisitor(
            ti, hopNames: ["Outer", "Inner"], sources.Count, scopeParams);
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
    /// Translates one operand of a cross-scope leaf: single-scope or scope-free (constant/parameter) operands via the
    /// shared re-root-and-prefix path, nested cross-scope operands by recursion.
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
    /// Shared re-root / translate / prefix core for the single-scope leaf and per-operand cross-scope paths.
    /// </summary>
    /// <remarks>
    /// With <paramref name="requireScopeRooted"/>, a leaf with no scope-rooted member (<c>2m * 3m</c>) declines
    /// rather than pushing down a constant-only <c>$project</c> leaf. A scope-free operand (the <c>1</c> in
    /// <c>(o.Rank * i.Price) + 1</c>) is fine against <c>translators[0]</c>, since it contains no field reference.
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

        var visitor = new MongoTransparentScopeResolver.ScopeRerootingVisitor(
            ti, hopNames: ["Outer", "Inner"], sources.Count, scopeParams);
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

        if (scope == 0)
        {
            result = computed;
            return true;
        }

        return MongoFieldPrefixRewriter.TryRewrite(computed, sources[scope - 1].InnerScopePath, out result);
    }

    /// <summary>
    /// Translates a member access already rooted on its scope's parameter with the outer or inner translator,
    /// prefixing an inner field with <paramref name="unwindPath"/>. Shared by <see cref="TryBind"/> and
    /// <see cref="TryBindTransparentIdentifierProjection"/>.
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
    /// Peels <c>Where(...)</c> layers off an owned collection selector down to the nav access, collecting their
    /// predicates. Owned collections have no FK-correlation <c>Where</c>, so every layer is a user filter.
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
    /// Translates the peeled owned-element predicates and ANDs them into <paramref name="filter"/>. Inner-only
    /// layers are prefixed with <paramref name="unwindPath"/> (<c>Price</c> becomes <c>Items.Price</c>); layers
    /// referencing the outer parameter (e.g. <c>i.Name == o.Name</c>) use the two-scope translator, routed by
    /// parameter identity so a shared member name can't mis-scope, rendering as <c>$expr</c>. Returns
    /// <see langword="true"/> with a null filter when there are no predicates. A decline hard-fails in every mode,
    /// since a correlated owned <c>SelectMany</c> has no driver-LINQ oracle.
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
            if (userPredicate.Body.ReferencesParameter(outerParam))
            {
                // Two-scope translation is already scoped; don't blanket-prefix.
                var twoScope = new MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, unwindPath);
                if (!twoScope.TryTranslate(userPredicate.Body, out var correlated))
                    return false;
                conjunct = correlated;
            }
            else
            {
                if (!innerTranslator.TryTranslate(userPredicate.Body, out var expr)
                    || !MongoFieldPrefixRewriter.TryRewrite(expr, unwindPath, out var prefixed))
                    return false;

                conjunct = prefixed;
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

}
