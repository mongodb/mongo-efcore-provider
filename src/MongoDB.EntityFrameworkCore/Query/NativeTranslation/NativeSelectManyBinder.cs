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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        var navExpr = UnwrapAsQueryable(selectSource);
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

        mongoQ.Select.UnwindSource = new MongoUnwindSource(unwindPath, navigation.TargetEntityType);
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

        var navExpr = UnwrapAsQueryable(collectionSelector.Body);
        if (!TryGetMemberAccess(navExpr, out var navRoot, out var navName) || !ReferenceEquals(navRoot, outerParam))
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var navigation = outerEntityType.FindNavigation(navName);
        if (navigation is not { IsCollection: true } || !navigation.TargetEntityType.IsOwned())
            return false;
        if (navigation.TargetEntityType.GetContainingElementName() is not { } unwindPath)
            return false;

        mongoQ.Select.UnwindSource = new MongoUnwindSource(unwindPath, navigation.TargetEntityType);
        return true;
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

            if (!TryTranslateScopedField(outerTranslator, innerTranslator, unwind.ElementPath, rerooted, isInner, out var field))
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
