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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure; // IsEFPropertyMethod()
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;           // QueryableMethods
using MongoDB.EntityFrameworkCore.Extensions;        // GetDocumentPath(), IsEmbedded()
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — member resolution. Resolves a member-access / <c>EF.Property</c>
/// chain to an <see cref="IProperty"/> and its MongoDB document path, in every position (predicate, sort key
/// and projection leaf all reach <see cref="MongoExpressionTranslator.TryResolveMember"/>).
/// </summary>
/// <remarks>
/// These members read the private scope state
/// (<c>_entityType</c>/<c>_outerParam</c>/<c>_outerEntityType</c>/<c>_innerPrefix</c>), which is why this is
/// a <c>partial</c> split rather than an extracted type: the by-name-retarget hazard documented on
/// <see cref="TryResolveOwnedCollectionPath"/> is exactly a hazard about which scope a member resolves against.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    /// <summary>
    /// Attempts to resolve a simple member-access expression to its <see cref="IProperty"/> and
    /// the MongoDB document element name or path. Returns <see langword="false"/> for any property that
    /// cannot be natively addressed. Composite-PK components are resolved via the <c>_id.&lt;element&gt;</c>
    /// dotted path, since the serializer nests a composite key under a local <c>_id</c> scoped to whichever
    /// entity type declares it — the document root's own composite key under the document's own top-level
    /// <c>_id</c>, and a NESTED owned type's own explicit composite key (e.g. an OwnsOne configured with a
    /// multi-property <c>HasKey</c>) under an <c>_id</c> local to that embedded subdocument. So this check is
    /// unconditional on the declaring type's root-ness; see <see cref="TryResolveOwnedFieldPath"/>'s leaf
    /// construction for the composed (scope-relative-prefix + local "_id.&lt;name&gt;" suffix) case.
    /// </summary>
    private bool TryResolveMember(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath,
        out bool isOuter)
    {
        property = null;
        fieldPath = null;
        isOuter = false;

        // Peel Nullable<T>.Value: `x.A.Value` is a MemberExpression whose receiver is the member access we
        // actually want, so without this it misses the fast path below and is handed to the owned dotted-path
        // resolver, which walks hops requiring embedded navigations and declines. The peel is safe because the
        // resolved property keeps its own nullability — `.Value` changes the CLR type, never the stored
        // element — so the emitted field ref is identical to the one `x.A` produces.
        //
        // The `Nullable.GetUnderlyingType(...) is not null` conjunct is load-bearing, not a redundant sibling
        // of the name test: a user type may declare its own member called `Value`, and when that user type is
        // the CLR type of a mapped scalar property (a value-converted strongly-typed id, say), peeling it would
        // resolve the receiver — silently answering a question about `x.Code` when the query asked about
        // `x.Code.Value`, and bypassing the value converter while doing so. Pinned by
        // MongoExpressionTranslatorTests.A_user_type_member_named_Value_is_NOT_peeled.
        while (node is MemberExpression { Member.Name: nameof(Nullable<int>.Value), Expression: { } nullableReceiver }
               && Nullable.GetUnderlyingType(nullableReceiver.Type) is not null)
        {
            node = nullableReceiver;
        }

        // Fast path: a top-level scalar access on the query parameter, in either spelling EF produces — a
        // bare member (p.Foo) or the shadow-safe EF.Property<T>(p, "Foo") call. Both name one hop off the
        // parameter and must resolve identically.
        //
        // Everything else — a member rooted on another hop, or a multi-hop EF.Property chain from owned-nav
        // expansion — is delegated to the owned dotted-path resolver, which declines cleanly for any shape
        // that is not a valid owned chain (including a single hop, which this fast path already handles).
        ParameterExpression param;
        string memberName;
        switch (node)
        {
            case MemberExpression { Expression: ParameterExpression memberParam } me:
                param = memberParam;
                memberName = me.Member.Name;
                break;

            // The EF.Property spelling, single hop only: EF.Property<T>(param, "Name"). Unwrap is applied to
            // the receiver because EF's own nav-expansion emits a BARE parameter there while the C# compiler
            // may wrap it in a Convert-to-object for EF.Property's `object entity` parameter — the two must
            // resolve identically, and Unwrap strips exactly that. A receiver that is anything else after
            // unwrapping is a MULTI-hop chain and belongs to the owned dotted-path resolver, unchanged.
            case MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [var receiver, ConstantExpression { Value: string name }]
                     && Unwrap(receiver) is ParameterExpression callParam:
                param = callParam;
                memberName = name;
                break;

            default:
                return TryResolveOwnedFieldPath(node, out property, out fieldPath, out isOuter);
        }

        // Two-scope mode: a member rooted on the outer param resolves against the outer entity type at document
        // root; every other member is inner-scoped. Identity (ReferenceEquals), never name — so a member name
        // shared between the two scopes cannot be mis-routed.
        isOuter = _outerParam is not null && ReferenceEquals(param, _outerParam);
        var scopeType = isOuter ? _outerEntityType! : _entityType;

        var resolved = scopeType.FindProperty(memberName);
        if (resolved is null)
            return false;

        property = resolved;
        fieldPath = IsCompositeKeyComponent(resolved)
            ? "_id." + resolved.GetElementName()
            : resolved.GetElementName();

        // Inner-scope fields are prefixed with the unwind scope in two-scope mode; outer-scope fields (and every
        // field in single-scope mode, where _innerPrefix is null) stay at their resolved element name.
        if (!isOuter && _innerPrefix is not null)
            fieldPath = _innerPrefix + "." + fieldPath;

        return true;
    }

    /// <summary>
    /// Resolves a nested member/navigation access chain into an owned single-reference (OwnsOne) dotted
    /// document path, e.g. <c>p.Address.City</c> → element path <c>"Address.City"</c> and the <c>City</c>
    /// property. Each hop may be a <see cref="MemberExpression"/> (scalar access) or an
    /// <c>EF.Property(root, "Nav")</c> call (the shadow-nav-safe form EF's nav-expansion rewrites owned-nav
    /// access into); every non-leaf hop must resolve to an embedded single-reference navigation, and the chain
    /// must be rooted at the query parameter with a mapped scalar leaf. Returns <see langword="false"/> (caller
    /// falls back to driver-LINQ) for any other shape.
    /// </summary>
    /// <remarks>
    /// <b>Scope-relative by construction (mirrors <see cref="TryResolveOwnedCollectionPath"/>).</b> The path is
    /// built by joining each intermediate hop's own navigation <see cref="MongoEntityTypeExtensions.GetContainingElementName"/>,
    /// relative to <c>_entityType</c> — NOT via <see cref="MongoEntityTypeExtensions.GetDocumentPath"/>, which
    /// is always relative to the TRUE document root and would double-prefix when <c>_entityType</c> is itself a
    /// non-root scope (e.g. an owned-collection-element translator built for a quantifier/<c>SelectMany</c>
    /// element predicate, whose caller separately prefixes the result with the unwind path). Because the result
    /// is relative to <c>_entityType</c>, it composes correctly whatever scope this translator was built on —
    /// there is deliberately no <c>IsDocumentRoot</c> guard here, same reasoning as
    /// <see cref="TryResolveOwnedCollectionPath"/>'s own remarks.
    /// <para>
    /// Two-scope mode (<c>_outerParam</c> set): a dotted chain rooted on the OUTER param resolves against
    /// <c>_outerEntityType</c> (EF-421) — the root identity is checked the same way
    /// <see cref="MongoExpressionTranslator.TryResolveMember"/> checks it for a single hop, never by name. A
    /// dotted chain reached from an <c>_innerPrefix</c>-set (SelectMany-unwind) scope, or rooted on neither known
    /// parameter, is still declined — that combination remains out of scope.
    /// </para>
    /// <para>
    /// <b>Why accepting any <see cref="ParameterExpression"/> root is safe</b> (same hazard, same resolution,
    /// as <see cref="TryResolveOwnedCollectionPath"/>'s own remarks on this point — EF-424 made this method
    /// reachable from a non-root scope, so the hazard now applies here too). The walk below (<c>current is not
    /// ParameterExpression</c>) does not check WHICH parameter roots the chain, so on its own it would resolve
    /// a member rooted on an enclosing parameter against this translator's own (wrong) scope type. That shape
    /// cannot reach here: the enclosing parameter is free in an element-predicate body, so the <c>Any</c>/<c>All</c>
    /// arm's <see cref="ReferencesEnclosingScope"/> guard — and <see cref="NativeSelectManyBinder"/>'s own
    /// parameter-identity-routed construction of the inner-filter translator — decline or correctly route any
    /// cross-scope reference before a single-scope, element-typed child translator is ever constructed. At the
    /// outermost level the only parameter in scope is the query parameter, so this is never actually reached
    /// with a "wrong" parameter identity.
    /// </para>
    /// </remarks>
    private bool TryResolveOwnedFieldPath(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath,
        out bool isOuter)
    {
        property = null;
        fieldPath = null;
        isOuter = false;

        // A dotted chain reached from an INNER-prefixed scope (SelectMany's unwind prefix) still declines: that
        // shape is a different, still-out-of-scope combination this ticket does not address (a dotted owned
        // path reached from inside a SelectMany element, itself further correlated). Only the "root is the
        // OUTER param" two-scope case is relativized here — see EF-421 §6.
        if (_innerPrefix is not null)
            return false;

        // Collect hop names from the outer (leaf) hop inward; the root must be a parameter.
        var names = new List<string>();
        var current = node;
        while (TryGetMemberOrEFProperty(current, out var inner, out var name))
        {
            names.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression rootParam)
            return false;

        // A single top-level member is handled by TryResolveMember's fast path, never here.
        if (names.Count < 2)
            return false;

        // EF-421: resolve the hop's ROOT identity, mirroring TryResolveMember's own isOuter check — never by
        // name. A two-scope translator whose root is the OUTER param resolves against the outer entity type,
        // at the outer document's OWN root (segments accumulate relative to _outerEntityType, exactly as the
        // existing code below already does relative to _entityType — the scopeType seed is the only change).
        // A single-scope translator (_outerParam is null) is unaffected: isOuter is always false there.
        isOuter = _outerParam is not null && ReferenceEquals(rootParam, _outerParam);
        if (_outerParam is not null && !isOuter)
            return false; // two-scope mode, but rooted on neither known parameter — not reachable in practice,
                           // decline rather than guess which scope it belongs to

        names.Reverse(); // now root-first: [firstNav, ..., leaf]

        var scopeType = isOuter ? _outerEntityType! : _entityType;
        var segments = new List<string>(names.Count);
        for (var i = 0; i < names.Count - 1; i++)
        {
            var navigation = scopeType.FindNavigation(names[i]);
            if (navigation is null || !navigation.IsEmbedded() || navigation.IsCollection)
            {
                // Cross-collection or owned-collection intermediate: fall back. An array intermediate has no
                // single dotted path to address, so a leaf underneath one (e.g. b.Posts[..].Title as a
                // predicate/sort/projection leaf) has no native form here at all. An Any/All quantifier over
                // the same collection is a different resolver (TryResolveOwnedCollectionPath) and does go
                // native — this decline does not cover quantifiers.
                return false;
            }

            // The navigation's containing element name is the same source the shapers and pipeline use, so
            // the emitted path matches stored layout (including HasElementName overrides and shared types),
            // and — unlike GetDocumentPath() — is relative to _entityType rather than the true document root.
            var elementName = navigation.TargetEntityType.GetContainingElementName();
            if (string.IsNullOrEmpty(elementName))
                return false;

            segments.Add(elementName);
            scopeType = navigation.TargetEntityType;
        }

        var leaf = scopeType.FindProperty(names[^1]);
        if (leaf is null)
            return false;

        property = leaf;
        // A composite-PK leaf nests under a LOCAL "_id" scoped to scopeType (the leaf's own declaring type),
        // not the true document root's "_id" — so appending "_id.<name>" here as ONE more segment on top of
        // the scope-relative hop prefix already accumulated above is exactly right (e.g. "Author._id.City"
        // for a leaf whose OWN declaring type, reached via one hop, has an explicit composite key). See
        // IsCompositeKeyComponent's remarks for why this holds regardless of scopeType's root-ness.
        var leafElementName = IsCompositeKeyComponent(leaf) ? "_id." + leaf.GetElementName() : leaf.GetElementName();
        segments.Add(leafElementName);
        fieldPath = string.Join(".", segments);
        return true;
    }

    /// <summary>
    /// True when <paramref name="property"/> is a component of a composite primary key (2+ properties) — the
    /// one case where a property's own element name does not address the stored field, because the
    /// serializer nests a composite key under a LOCAL <c>_id</c> scoped to whichever entity type declares it:
    /// <c>{ _id: { Key1, Key2 } }</c> at the document root, or e.g. <c>{ Author: { _id: { City, Country } } }</c>
    /// for a NESTED owned type given its own explicit multi-property <c>HasKey</c> (verified: the serializer
    /// nests an embedded "_id" in exactly this shape regardless of nesting depth, so this check is
    /// deliberately unconditional on the declaring type's root-ness — EF-424).
    /// </summary>
    private static bool IsCompositeKeyComponent(IProperty property)
        => property.IsPrimaryKey() && property.FindContainingPrimaryKey()!.Properties.Count > 1;

    // A single access hop in either shape EF produces: a plain MemberExpression (scalar access) or an
    // EF.Property(root, "Name") call (owned-nav expansion). Mirrors NativeSelectManyBinder.TryGetMemberAccess.
    private static bool TryGetMemberOrEFProperty(Expression expression, out Expression inner, out string name)
    {
        switch (expression)
        {
            case MemberExpression { Expression: { } e } member:
                inner = e;
                name = member.Member.Name;
                return true;

            case MethodCallExpression call
                when call.Method.IsEFPropertyMethod()
                     && call.Arguments is [var root, ConstantExpression { Value: string propName }]:
                inner = root;
                name = propName;
                return true;

            default:
                inner = null!;
                name = null!;
                return false;
        }
    }

    /// <summary>
    /// Resolves the SOURCE of an owned-collection quantifier (<c>b.Posts</c>, <c>b.Address.Notes</c>) to the
    /// dotted document path of the embedded array — <b>relative to this translator's scope entity type</b> —
    /// and yields the array's element entity type. Every non-final hop must be an embedded single-reference
    /// navigation; the final hop must be an embedded collection navigation; the chain must be rooted at the
    /// query parameter. Returns <see langword="false"/> (caller falls back to driver-LINQ) for anything else,
    /// including a reference (non-embedded) navigation and a primitive collection property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why scope-relative</b> (same construction as <see cref="TryResolveOwnedFieldPath"/> uses for its
    /// dotted-path leaf, EF-424): this method joins the hop navigations' own containing element names, so the
    /// path is relative to <c>_entityType</c> by construction — never via
    /// <see cref="MongoEntityTypeExtensions.GetDocumentPath"/>, which is always relative to the TRUE document
    /// root and would double-prefix when <c>_entityType</c> is itself a non-root scope whose caller separately
    /// prefixes the result. Neither this method nor <see cref="TryResolveOwnedFieldPath"/> has an
    /// <c>IsDocumentRoot</c> guard — a scope-relative path composes correctly with
    /// <see cref="MongoFieldPrefixRewriter"/> prepending rather than fighting it, and it is what makes a
    /// nested <c>Any</c>-within-<c>Any</c> correct: the element-scoped child translator resolves the inner
    /// array relative to the element, which is exactly what the enclosing <c>$elemMatch</c> expects.
    /// </para>
    /// <para>
    /// Two-scope mode (<c>_outerParam</c> set): a chain rooted on the OUTER param resolves against
    /// <c>_outerEntityType</c> (EF-421), by the same root-identity check as
    /// <see cref="TryResolveOwnedFieldPath"/>. The array itself being reached through the outer scope
    /// (a quantifier over an outer sibling collection) is left to the caller to decide — see
    /// <see cref="MongoExpressionTranslator"/>'s quantifier/<c>Count</c> arms, which decline that combination.
    /// A chain reached from an <c>_innerPrefix</c>-set (SelectMany-unwind) scope, or rooted on neither known
    /// parameter, is still declined outright.
    /// </para>
    /// <para>
    /// <b>Why accepting any <see cref="ParameterExpression"/> root is safe.</b> This walk does not check which
    /// parameter roots the chain, so on its own it would resolve a source rooted on an enclosing parameter
    /// (<c>b.Posts.Any(p =&gt; b.Posts.Any(q =&gt; …))</c>) against this translator's own scope type. That shape cannot
    /// reach here: the enclosing parameter is free in the element-predicate body, so the <c>Any</c> arm's
    /// <see cref="ReferencesEnclosingScope"/> guard declines the whole quantifier before the element-scoped child
    /// translator is even constructed. At the outermost level the only parameter in scope is the query parameter.
    /// </para>
    /// </remarks>
    private bool TryResolveOwnedCollectionPath(
        Expression source,
        [NotNullWhen(true)] out string? arrayPath,
        [NotNullWhen(true)] out IEntityType? elementType,
        out bool isOuter)
    {
        arrayPath = null;
        elementType = null;
        isOuter = false;

        // Same restriction as TryResolveOwnedFieldPath: an inner-prefixed (SelectMany unwind) scope still
        // declines outright — only the "root is the OUTER param" case is relativized.
        if (_innerPrefix is not null)
            return false;

        // Collect hop names from the outer hop inward; the root must be a parameter.
        var names = new List<string>();
        var current = source;
        while (TryGetMemberOrEFProperty(current, out var inner, out var name))
        {
            names.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression rootParam || names.Count == 0)
            return false;

        isOuter = _outerParam is not null && ReferenceEquals(rootParam, _outerParam);
        if (_outerParam is not null && !isOuter)
            return false; // two-scope mode, rooted on neither known parameter — decline

        names.Reverse(); // now root-first: [ownedRefNav, ..., collectionNav]

        var scopeType = isOuter ? _outerEntityType! : _entityType;
        var segments = new List<string>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var navigation = scopeType.FindNavigation(names[i]);
            if (navigation is null || !navigation.IsEmbedded())
                return false; // a primitive collection property or a reference nav has no embedded path

            // A collection is allowed only as the FINAL hop (it is the quantifier's source); every
            // intermediate hop must be an owned single reference.
            if (navigation.IsCollection != (i == names.Count - 1))
                return false;

            // The navigation's containing element name is the same source the shapers and pipeline use, so
            // the emitted path matches stored layout (including HasElementName overrides and shared types).
            var elementName = navigation.TargetEntityType.GetContainingElementName();
            if (string.IsNullOrEmpty(elementName))
                return false;

            segments.Add(elementName);
            scopeType = navigation.TargetEntityType;
        }

        arrayPath = string.Join(".", segments);
        elementType = scopeType;
        return true;
    }
}
