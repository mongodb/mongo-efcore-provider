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
/// Binds owned-collection <c>SelectMany</c> to a native <c>$unwind</c> + <c>$project</c>, across BOTH
/// user-authored shapes EF's nav-expansion can produce. <see cref="TryBind"/> handles the INNER-<c>Select</c>
/// form (EF-347 slice 3) — <c>o => o.Items.AsQueryable().Select(i => new { o.X, i.Y })</c> — where the
/// projection is nested inside the collection selector itself. <see cref="TryBindBareNavUnwind"/> +
/// <see cref="TryBindTransparentIdentifierProjection"/> together handle the explicit-result-selector /
/// query-syntax form (EF-347 slice 4) — <c>SelectMany(o => o.Items, (o, i) => new { o.X, i.Y })</c> / <c>from o
/// in q from i in o.Items select new { o.X, i.Y }</c> — which normalizes to a BARE owned-nav collection
/// selector (no nested <c>Select</c>) plus a SEPARATE trailing <c>Select</c> over the
/// <c>TransparentIdentifier(Outer, Inner)</c> result: <see cref="TryBindBareNavUnwind"/> sets
/// <see cref="MongoSelectDefinition.UnwindSource"/> from the bare nav alone, and
/// <see cref="TryBindTransparentIdentifierProjection"/> — invoked separately, from the trailing <c>Select</c> —
/// binds that Select's <c>ti.Outer</c>/<c>ti.Inner</c> member accesses into <see cref="MongoSelectDefinition.Projection"/>.
/// All three binders resolve outer (closed-over) members to root field refs (<c>$X</c>) and inner
/// (collection-element) members to the unwound element, prefixed with the unwind path (<c>$Items.Y</c>), via
/// two structurally separate <see cref="MongoExpressionTranslator"/>s so a member name shared between the two
/// scopes (e.g. both having a <c>Name</c>) never resolves against the wrong one. Each returns
/// <see langword="false"/> (select/projection untouched) for any shape outside its own scope — for
/// <see cref="TryBind"/> and <see cref="TryBindBareNavUnwind"/> the caller then returns <see langword="null"/>
/// and EF hard-fails translation, as before.
/// </summary>
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
        // rewrites navigation access to EF.Property(o, "Nav") (shadow-nav-safe) rather than leaving a plain
        // MemberExpression, so both forms must be accepted here.
        // Peel any user Where(...) layers off the owned nav (o.Items.Where(pred).Select(...)); owned collections
        // are a bare member access, so every Where is an inner-element user filter (no FK correlation).
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
        mongoQ.Select.UnwindSource = unwind;
        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        return true;
    }

    /// <summary>
    /// Binds the BARE-nav collection-selector shape of an owned-collection <c>SelectMany</c> (EF-347 slice 4)
    /// — <c>o =&gt; o.Items.AsQueryable()</c> (or <c>EF.Property(o,"Items")</c>), with NO nested <c>Select</c> —
    /// which is what EF's nav-expansion produces for BOTH the explicit-result-selector form
    /// (<c>SelectMany(o =&gt; o.Items, (o,i) =&gt; ...)</c>) and the query-syntax equivalent. Sets
    /// <see cref="MongoSelectDefinition.UnwindSource"/> only — the real projection is bound later, by
    /// <see cref="TryBindTransparentIdentifierProjection"/> against the SEPARATE trailing <c>Select</c>.
    /// Returns <see langword="false"/> (select untouched) for a nested-<c>Select</c> body (that is
    /// <see cref="TryBind"/>'s inner-<c>Select</c> form), a non-owned/reference navigation, or a
    /// non-collection navigation.
    /// </summary>
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
        mongoQ.Select.UnwindSource = unwind;
        return true;
    }

    /// <summary>
    /// Binds a cross-collection REFERENCE-nav <c>SelectMany</c> (EF-347 slice 5) — <c>SelectMany(c =&gt; c.Orders,
    /// (c, o) =&gt; new {...})</c> / <c>from c in q from o in c.Orders select new {...}</c> over a REFERENCE
    /// (non-embedded) collection navigation. Unlike the owned bare-nav shape (<see cref="TryBindBareNavUnwind"/>),
    /// EF's nav-expansion normalizes a reference collection selector to a CORRELATED SUBQUERY — spike-confirmed
    /// (<c>.superpowers/sdd/explicit-selectmany-spike.md</c>) and design-doc-confirmed — of the form
    /// <c>Queryable.Where(EntityQueryRootExpression&lt;Target&gt;, o =&gt; c.pk == o.fk)</c> (possibly wrapped in
    /// <c>AsQueryable</c>), NOT a bare nav: the target collection is queried from its own root and filtered by
    /// the FK correlation, rather than read off the outer entity directly. Recognizes that shape via
    /// <see cref="NativeCorrelationMatcher.TryMatchCorrelatedCollection"/> (shared with
    /// <see cref="NativeProjectionBinder"/>'s projected-<c>Count</c> recognition — EF-347 slice 5, Task 1),
    /// requiring a REFERENCE (<c>requireEmbedded: false</c>) navigation — the mirror of
    /// <see cref="TryBindBareNavUnwind"/>'s owned-only acceptance, so the two binders partition the shape space
    /// rather than overlap. On a match, registers a <c>ForceUnwind</c> <c>$lookup</c> for the navigation and sets
    /// <see cref="MongoSelectDefinition.UnwindSource"/> to a <see cref="MongoUnwindSourceKind.Reference"/> source
    /// whose scope is the lookup's <c>_lookup_&lt;Nav&gt;</c> alias — the REAL projection is bound later, exactly
    /// as for the owned bare-nav shape, by the UNCHANGED <see cref="TryBindTransparentIdentifierProjection"/>
    /// against the SEPARATE trailing <c>Select</c> (its <c>InnerScopePath</c> read is scope-kind-agnostic, so
    /// the generalized reference scope flows through with no changes there).
    /// </summary>
    internal static bool TryBindReferenceNavUnwind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        // Peel user-predicate Where layers. A filtered inner c.Refs.Where(p1).Where(p2) nav-expands to
        // Where(Where(Where(root, fkPred), p1), p2): the innermost Where over the query root carries the FK
        // correlation EF injects; every OUTER Where is an inner-element-only user filter. (A single Where whose
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

        // Isolate the FK correlation (→ the reference navigation) from any user conjunct FOLDED into the
        // innermost predicate; the shared matcher's own reject-extra-conjunct contract is untouched.
        if (!TrySplitCorrelation(predicate.Body, outerEntityType, outerParam, root.EntityType,
                out var navigation, out var foldedUserBody))
            return false;

        // Translate each user filter (peeled Where layers + any folded conjunct) via TryTranslateReferenceFilterLayer,
        // ANDing the results into one predicate. A layer referencing only the inner element translates against the
        // inner target entity type and gets its field refs prefixed with the $lookup scope; a layer that also
        // references outer members beyond the FK (correlated-beyond-FK) is routed to the two-scope translator
        // instead, which resolves outer members at document root and inner members under the $lookup scope. Any
        // filter neither translator can handle declines cleanly, with no partial mutation of mongoQ.
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
        // this call would be a no-op and UnwindSource.Lookup below would point at an instance that was NOT the
        // one actually in the pending list. That collision is UNREACHABLE for a reference SelectMany, confirmed
        // once the QMTEV wiring (Task 4) made this method reachable end-to-end: reference SelectMany is
        // projected-only (a bare-entity trailing selector hard-declines before reaching here — see
        // TranslateSelect's whole-inner-entity guard), and EF Core drops any Include not applied to the query's
        // final materialized entity — so a SelectMany's anonymous/DTO projection result carries no live
        // same-nav Include to have registered a colliding lookup in the first place. This always registers our
        // own instance.
        mongoQ.AddLookup(lookup);
        var unwind = MongoUnwindSource.Reference(scope, navigation.TargetEntityType, lookup);
        unwind.Filter = filter;
        mongoQ.Select.UnwindSource = unwind;
        return true;
    }

    /// <summary>
    /// Resolves the FK-correlated reference navigation from the innermost <c>Where</c> predicate, isolating it
    /// from any inner-element user filter FOLDED into the same predicate (<c>fkPred &amp;&amp; userPred</c>).
    /// The shared <see cref="NativeCorrelationMatcher"/> is only ever fed the isolated FK-correlation
    /// expression, so its reject-extra-conjunct contract (which keeps a filtered <c>Count</c> on fallback) is
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// The folded branch is DEFENSIVE-ONLY: EF's nav-expansion emits the NESTED shape
    /// (<c>Where(Where(root, fkPred), userPred)</c>, spike-confirmed on EF8/EF9/EF10), whose user predicates the
    /// caller peels off as separate <c>Where</c> layers BEFORE this method ever sees a folded predicate — so no
    /// real query reaches the folded branch today. It is best-effort, with a KNOWN LIMITATION (EF-355): a user
    /// conjunct shaped <c>x != null</c> folded with the FK equality is swallowed by the matcher's null-guard
    /// handling (<see cref="NativeCorrelationMatcher"/>'s null-guard check does not verify the guarded key
    /// matches the FK key), so the whole-predicate match returns <c>userBody == null</c> and that user filter is
    /// SILENTLY DROPPED. Harmless while the branch is unreachable; a correct fix — distinguishing an outer-key
    /// null-guard from an inner-element user <c>!= null</c> without touching the shared matcher or regressing a
    /// legitimately null-guarded nested FK correlation — is tracked by EF-355.
    /// </remarks>
    private static bool TrySplitCorrelation(
        Expression predicateBody, IEntityType outerEntityType, ParameterExpression outerParam,
        IEntityType targetEntityType, out INavigation navigation, out Expression? userBody)
    {
        userBody = null;

        // Nested / pure FK correlation: the whole innermost predicate IS the FK correlation.
        if (NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                predicateBody, outerEntityType, outerParam, targetEntityType, requireEmbedded: false, out navigation))
            return true;

        // Folded: fkPred && userPred. Flatten top-level AndAlso conjuncts, find the ONE that is the FK
        // correlation, recombine the rest as the user filter.
        var conjuncts = new List<Expression>();
        FlattenAndAlso(predicateBody, conjuncts);
        if (conjuncts.Count < 2)
            return false;

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

        if (fkConjunct == null || rest.Count == 0)
            return false;

        userBody = rest.Aggregate(Expression.AndAlso);
        return true;
    }

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
    /// <see cref="TryTranslateReferenceFilterLayer"/> to detect a user filter that is correlated beyond the FK
    /// equality already isolated by <see cref="TrySplitCorrelation"/>, and route it to the two-scope translator
    /// instead of the single-scope one. The single-scope <see cref="MongoExpressionTranslator"/> resolves a
    /// member access by NAME against whichever entity type it was constructed for, with no parameter-identity
    /// check (it never needs one for its other callers, which only ever translate a single-parameter lambda
    /// body). Without this check, an outer-scoped member access that happens to share a property name with the
    /// inner target entity (e.g. both having an "Id" property) would silently mistranslate as an inner-scoped
    /// reference instead of being routed to the correctly-scoped translator.
    /// </summary>
    private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
    {
        var visitor = new ParameterReferenceVisitor(parameter);
        visitor.Visit(expression);
        return visitor.Found;
    }

    /// <summary>
    /// Translates one peeled reference-<c>SelectMany</c> inner-filter <c>Where</c> layer into a filter conjunct.
    /// A layer that references the outer <c>SelectMany</c> parameter (correlated beyond the FK) is translated with
    /// the two-scope translator — inner field refs prefixed with <paramref name="scope"/>, outer field refs at
    /// document root, routed by parameter identity so a shared member name never conflates the scopes — and its
    /// field-to-field comparison is later rendered as <c>$expr</c>. A layer that references only the inner element
    /// keeps the single-scope translate + blanket-prefix path unchanged. Returns <see langword="false"/> (with no
    /// mutation of the caller's query) when the layer cannot be translated.
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
    /// <c>SelectMany</c> (EF-347 slice 4) — <c>SelectMany(o =&gt; o.Items, (o, i) =&gt; new {...})</c> and its
    /// query-syntax equivalent. EF's nav-expansion normalizes both to the bare-nav collection-selector form
    /// (accepted elsewhere by the bare-nav path, Task 2) wrapped in a <c>TransparentIdentifier(Outer, Inner)</c>
    /// result selector; the REAL projection is a SEPARATE trailing <c>Select(ti =&gt; new {ti.Outer.X, ti.Inner.Y})</c>
    /// over that transparent identifier (spike-confirmed) — this method binds THAT trailing Select, given a
    /// query whose <see cref="MongoSelectDefinition.UnwindSource"/> is already set (by the bare-nav bind, Task 2)
    /// and whose <see cref="MongoSelectDefinition.Projection"/> is still empty.
    /// </summary>
    /// <remarks>
    /// Each projection leaf is a nested member access on the single <c>ti</c> parameter —
    /// <c>MemberExpression(MemberExpression(ti, "Outer"|"Inner"), &lt;member&gt;)</c> — NOT pre-folded by EF. Because
    /// <see cref="MongoExpressionTranslator.TryTranslateField"/> only resolves a <see cref="MemberExpression"/>
    /// whose own <c>Expression</c> is a bare <see cref="ParameterExpression"/> (it rejects <c>ti.Outer.X</c>
    /// outright, since <c>ti.Outer</c> is itself a <see cref="MemberExpression"/>, not a parameter), each leaf's
    /// member is re-rooted onto a synthetic parameter of the scope's own entity CLR type before translation —
    /// the same two structurally-separate translators (outer vs. inner) the bare-nav/inner-Select bind
    /// (<see cref="TryBind"/>) already uses, just fed a re-rooted expression instead of the original inner-Select
    /// parameter's own member accesses.
    /// </remarks>
    internal static bool TryBindTransparentIdentifierProjection(MongoQueryExpression mongoQ, LambdaExpression selector)
    {
        if (mongoQ.Select.UnwindSource is not { } unwind || mongoQ.Select.Projection.Count > 0)
            return false;
        if (selector.Parameters.Count != 1)
            return false;
        var ti = selector.Parameters[0];

        if (!TryReadProjection(selector.Body, out var members))
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var outerTranslator = new MongoExpressionTranslator(outerEntityType);
        var innerTranslator = new MongoExpressionTranslator(unwind.InnerEntityType);
        var outerParam = Expression.Parameter(outerEntityType.ClrType, "o");
        var innerParam = Expression.Parameter(unwind.InnerEntityType.ClrType, "i");
        var projections = new List<MongoProjection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, argExpr) in members)
        {
            // Leaf must be ti.<scope>.<member> — a member access whose target is ti.Outer or ti.Inner.
            if (argExpr is not MemberExpression { Expression: MemberExpression scopeAccess } member
                || scopeAccess.Expression != ti
                || scopeAccess.Member.Name is not ("Outer" or "Inner"))
                return false;

            // Re-root the leaf's member onto a synthetic parameter of the scope's entity CLR type: the
            // translator only accepts a MemberExpression whose Expression is a bare ParameterExpression.
            var isInner = scopeAccess.Member.Name == "Inner";
            var rerooted = Expression.MakeMemberAccess(isInner ? innerParam : outerParam, member.Member);

            if (!TryTranslateScopedField(outerTranslator, innerTranslator, unwind.InnerScopePath, rerooted, isInner, out var field))
                return false;

            if (!seen.Add(alias)) return false;
            projections.Add(new MongoProjection(alias, field));
        }

        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        return true;
    }

    /// <summary>
    /// Translates a single already-scope-rooted member access (i.e. its <c>Expression</c> is a bare
    /// <see cref="ParameterExpression"/> of the target scope's own CLR type — <see cref="TryBind"/> passes the
    /// original inner-<c>Select</c>-parameter member access straight through; <see cref="TryBindTransparentIdentifierProjection"/>
    /// re-roots its <c>ti.Outer.&lt;m&gt;</c>/<c>ti.Inner.&lt;m&gt;</c> leaf onto a synthetic parameter first) via
    /// whichever of <paramref name="outerTranslator"/>/<paramref name="innerTranslator"/> matches
    /// <paramref name="isInner"/>, prefixing an inner match's element name with <paramref name="unwindPath"/> —
    /// the one piece of logic both binders share once each has resolved which scope a leaf belongs to by its
    /// own, structurally different means.
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
    /// Owned collections nav-expand to a bare member access (<c>o.Items</c>), NOT an FK-correlated subquery, so
    /// EVERY <c>Where</c> here is an inner-element user filter — there is no FK-correlation <c>Where</c> to stop
    /// at (unlike <see cref="TryBindReferenceNavUnwind"/>). Returns the source with all <c>Where</c> layers
    /// removed (the bare owned nav for an accepted shape); the caller validates it via <see cref="TryGetMemberAccess"/>.
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
    /// prefixed with <paramref name="unwindPath"/> (e.g. <c>Items</c>, so <c>Price</c> becomes
    /// <c>Items.Price</c> — the unwound owned element sits at that path before
    /// <c>$replaceRoot</c>/<c>$project</c>). A layer that references the outer parameter
    /// (correlated-beyond-outer, e.g. <c>i.Name == o.Name</c>) is instead ROUTED to the two-scope
    /// <see cref="MongoExpressionTranslator"/> — <see cref="ReferencesParameter"/> decides routing by PARAMETER
    /// IDENTITY, never by member name, so a name shared between the outer and inner entity types (e.g. both
    /// having a <c>Name</c>) never mis-scopes: the inner side still resolves against
    /// <paramref name="innerEntityType"/> prefixed with <paramref name="unwindPath"/>, the outer side against
    /// <paramref name="outerEntityType"/> at document root, and the result renders as <c>$expr</c>. Returns
    /// <see langword="true"/> with <paramref name="filter"/> <see langword="null"/> when there are no
    /// predicates (the unfiltered case), so callers can invoke it unconditionally. Declines
    /// (<see langword="false"/>, no mutation) only if a translator rejects the layer (computed / unsupported
    /// operator) — a correlated owned <c>SelectMany</c> has no driver-LINQ oracle, so a decline hard-fails
    /// translation in every mode.
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
                // Correlated-beyond-outer: translate with the two-scope translator — inner fields prefixed with
                // the unwind path (Items.Name), outer fields at document root (Name), routed by PARAMETER
                // IDENTITY (never by name, so Item.Name and Owner.Name never conflate). Used directly — NOT
                // blanket-prefixed. Renders as $expr. Declines cleanly (no mutation) if the operator is
                // unsupported; a correlated owned SelectMany has no driver-LINQ oracle, so a decline hard-fails
                // every mode.
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
    /// <see cref="MemberExpression"/> (used for ordinary scalar property access, e.g. the leaves of the
    /// nested <c>Select</c>'s projection) or an <c>EF.Property(root, "Name")</c> call (the shadow-nav-safe
    /// form EF's nav-expansion rewrites navigation access into, e.g. <c>o.Items</c> becoming
    /// <c>EF.Property(o, "Items")</c>). Returns the accessed root expression and member name for either shape.
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
