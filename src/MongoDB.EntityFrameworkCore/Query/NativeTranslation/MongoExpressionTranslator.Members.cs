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

        // EF-322 gap-3: a BARE-scalar Distinct's own key selector parameter (never a member access — the
        // projected result IS the scalar directly, e.g. Select(o => o.Country).Distinct().OrderBy(x =>
        // x.IndexOf(term))) resolves to the Distinct's sole flattened key part directly, by parameter IDENTITY
        // against SelfParam — same discipline as every other SelfParam use in this file, never by name. Scoped
        // to a SINGLE-key, FIELD-backed grouping (list-pattern match): a composite/named projection has real
        // members to access instead (handled by the ordinary DistinctAliasScope branch below), and a computed
        // sole key part has no IProperty this method could hand back (that shape, if it arises, still declines
        // here and falls back to driver-LINQ, exactly like any other unsupported member access).
        if (SelfParam is not null && ReferenceEquals(node, SelfParam)
            && DistinctAliasScope is { Key: [{ FieldRef: MongoFieldExpression soleField } soleKeyPart] })
        {
            property = soleField.Property;
            fieldPath = soleKeyPart.Name!;
            isOuter = false;
            return true;
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

        // EF-322: a post-Distinct predicate/sort-key member resolves against the Distinct's OWN flattened
        // output schema (DistinctAliasScope), never the entity — see DistinctAliasScope's remarks. A member
        // name that is not one of the Distinct's own key parts declines outright (does NOT fall through to
        // the entity below): the whole point is that a projected member's name is independent of any
        // real entity property of the same name, so falling through would silently resolve the wrong field.
        if (!isOuter && DistinctAliasScope is { } distinctScope)
        {
            foreach (var part in distinctScope.Key)
            {
                if (part.Name == memberName && part.FieldRef is MongoFieldExpression aliasField)
                {
                    property = aliasField.Property;
                    fieldPath = part.Name;
                    return true;
                }
            }

            return false;
        }

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
    /// EF-322 gap-2: resolves a single-hop member access on a Distinct alias whose flattened key part is a
    /// COMPUTED expression (e.g. <c>Select(c =&gt; new { A = c.CustomerID + c.City }).Distinct()</c>'s own
    /// <c>A</c>), which <see cref="TryResolveMember"/> cannot express — that method hands back an
    /// <see cref="IProperty"/>, and a computed key part has none. Used only by the small set of callers that
    /// need nothing but the flattened document path (currently the StartsWith/EndsWith/Contains regex case in
    /// <see cref="TryTranslate"/>'s main switch) — every other operator (equality, ordering, date/array
    /// functions, …) still resolves a computed alias member through <see cref="TryResolveMember"/> alone and
    /// correctly declines, since widening THAT method's IProperty-based contract to admit a property-less field
    /// would touch every one of its ~10 call sites for a single narrow shape.
    /// </summary>
    /// <remarks>
    /// Declines whenever <see cref="DistinctAliasScope"/> is unset, the node is not a single-hop member access,
    /// or the matched key part IS a plain field (that shape is already handled — and pinned — by
    /// <see cref="TryResolveMember"/>'s own <see cref="DistinctAliasScope"/> branch, so the two must never both
    /// claim the same member).
    /// </remarks>
    private bool TryResolveDistinctAliasComputedField(
        Expression node, [NotNullWhen(true)] out MongoElementRefExpression? fieldRef)
    {
        fieldRef = null;

        if (DistinctAliasScope is not { } scope || node is not MemberExpression { Expression: ParameterExpression } me)
            return false;

        foreach (var part in scope.Key)
        {
            if (part.Name == me.Member.Name && part.FieldRef is not MongoFieldExpression)
            {
                fieldRef = new MongoElementRefExpression(part.Name!, part.FieldRef.Type);
                return true;
            }
        }

        return false;
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
    /// <summary>
    /// The shared PREAMBLE of all three owned-path resolvers below: rejects an inner-prefixed scope, collects
    /// the access chain's hop names root-first, requires a <see cref="ParameterExpression"/> root, resolves
    /// WHICH scope that root names, and seeds the entity type to walk from.
    /// </summary>
    /// <param name="node">The member-access / <c>EF.Property</c> chain to walk.</param>
    /// <param name="minimumHops">
    /// The fewest hops the calling resolver can act on — 2 where the leaf is a scalar under at least one
    /// navigation (a single hop is <see cref="TryResolveMember"/>'s own fast path), 1 where the leaf is itself
    /// the navigation.
    /// </param>
    /// <param name="names">The hop names, ROOT-FIRST, on success.</param>
    /// <param name="scopeType">The entity type the first hop resolves against, on success.</param>
    /// <param name="isOuter">
    /// Whether the chain is rooted on this translator's OUTER parameter (always <see langword="false"/> in
    /// single-scope mode). Callers that cannot render an outer-scoped result must decline on it.
    /// </param>
    /// <remarks>
    /// <para>
    /// This was three near-identical copies, which had already diverged. It is the most invariant-critical code
    /// in this file: the <c>isOuter</c> line is the "scope is resolved by parameter IDENTITY, never by member
    /// name" rule from <c>Query/AGENTS.md</c>, whose failure mode is silently resolving a member against the
    /// WRONG scope rather than declining. Two types sharing a property name (<c>Item.Name</c> vs
    /// <c>Owner.Name</c>) is the standing regression test.
    /// </para>
    /// <para>
    /// <b>Scope-relative by construction.</b> Callers build their path by joining each hop navigation's own
    /// <see cref="MongoEntityTypeExtensions.GetContainingElementName"/> relative to the seeded
    /// <paramref name="scopeType"/> — never via <see cref="MongoEntityTypeExtensions.GetDocumentPath"/>, which
    /// is relative to the TRUE document root and would double-prefix when the scope is itself nested (an
    /// owned-collection-element translator built for a quantifier / <c>SelectMany</c> element predicate, whose
    /// caller separately prefixes the result). That is why there is deliberately no <c>IsDocumentRoot</c> guard
    /// anywhere in this family.
    /// </para>
    /// <para>
    /// <b>Why accepting any <see cref="ParameterExpression"/> root is safe in single-scope mode.</b> The walk
    /// does not check WHICH parameter roots the chain, so alone it would resolve a chain rooted on an enclosing
    /// parameter against this translator's own (wrong) scope type. That shape cannot reach here: the enclosing
    /// parameter is free in an element-predicate body, so the quantifier arm's
    /// <see cref="ReferencesEnclosingScope"/> guard — and <see cref="NativeSelectManyBinder"/>'s own
    /// parameter-identity-routed construction of the inner-filter translator — declines or correctly routes any
    /// cross-scope reference before a single-scope, element-typed child translator is ever built. At the
    /// outermost level the only parameter in scope is the query parameter.
    /// </para>
    /// </remarks>
    private bool TryBeginOwnedHopWalk(
        Expression node,
        int minimumHops,
        [NotNullWhen(true)] out List<string>? names,
        [NotNullWhen(true)] out IEntityType? scopeType,
        out bool isOuter)
    {
        names = null;
        scopeType = null;
        isOuter = false;

        // A chain reached from an INNER-prefixed scope (SelectMany's unwind prefix) declines: a dotted owned
        // path reached from inside a SelectMany element, itself further correlated, is a different and still
        // out-of-scope combination. Only the "root is the OUTER param" two-scope case is relativized.
        if (_innerPrefix is not null)
            return false;

        // Collect hop names from the outer (leaf) hop inward; the root must be a parameter.
        var hopNames = new List<string>();
        var current = node;
        while (current.TryGetMemberOrEFProperty(out var inner, out var name))
        {
            hopNames.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression rootParam || hopNames.Count < minimumHops)
            return false;

        isOuter = _outerParam is not null && ReferenceEquals(rootParam, _outerParam);
        if (_outerParam is not null && !isOuter)
            return false; // two-scope mode, rooted on neither known parameter — decline rather than guess

        hopNames.Reverse(); // now root-first: [firstNav, ..., leaf]

        names = hopNames;
        scopeType = isOuter ? _outerEntityType! : _entityType;
        return true;
    }

    /// <summary>
    /// Walks the first <paramref name="hopCount"/> hop names as embedded single-reference navigations,
    /// appending each one's containing element name to <paramref name="segments"/> and advancing
    /// <paramref name="scopeType"/> to the last walked hop's target.
    /// </summary>
    /// <remarks>
    /// Shared by all three resolvers for their INTERMEDIATE hops; each then applies its own rule to the final
    /// hop (a scalar property, a single-reference navigation, or a collection navigation). Declining here is
    /// what rejects a cross-collection or owned-collection intermediate: an array intermediate has no single
    /// dotted path to address, so a leaf underneath one (<c>b.Posts[..].Title</c> as a predicate/sort/projection
    /// leaf) has no native form at all. An <c>Any</c>/<c>All</c> quantifier over the same collection is
    /// <see cref="TryResolveOwnedCollectionPath"/>'s business and does go native — this decline does not cover
    /// quantifiers.
    /// </remarks>
    private static bool TryWalkEmbeddedReferenceHops(
        List<string> names, int hopCount, ref IEntityType scopeType, List<string> segments)
    {
        for (var i = 0; i < hopCount; i++)
        {
            var navigation = scopeType.FindNavigation(names[i]);
            if (navigation is null || !navigation.IsEmbedded() || navigation.IsCollection)
                return false;

            // The navigation's containing element name is the same source the shapers and pipeline use, so the
            // emitted path matches stored layout (including HasElementName overrides and shared types).
            var elementName = navigation.TargetEntityType.GetContainingElementName();
            if (string.IsNullOrEmpty(elementName))
                return false;

            segments.Add(elementName);
            scopeType = navigation.TargetEntityType;
        }

        return true;
    }

    private bool TryResolveOwnedFieldPath(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath,
        out bool isOuter)
    {
        property = null;
        fieldPath = null;

        if (!TryBeginOwnedHopWalk(node, minimumHops: 2, out var names, out var scopeType, out isOuter))
            return false;

        var segments = new List<string>(names.Count);
        if (!TryWalkEmbeddedReferenceHops(names, names.Count - 1, ref scopeType, segments))
            return false;

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

    /// <summary>
    /// Resolves an ENTITY-TYPED comparison operand — the whole root entity (<c>c</c> in <c>c == null</c>) or an
    /// owned/embedded single-reference navigation reached from it (<c>b.Address</c> in <c>b.Address == null</c>,
    /// including a multi-hop chain like <c>b.Address.Recipient</c>) — to a <see cref="MongoElementRefExpression"/>
    /// naming its document path. Used only by <see cref="TranslateComparison"/>'s entity-vs-null and
    /// entity-vs-itself arms; NOT a general entity-operand resolver (no caller may compare the result against
    /// anything other than a literal null, or against another identical entity-typed operand of the SAME
    /// origin — see those callers' own remarks for why).
    /// </summary>
    private bool TryResolveEntityTypedOperand(Expression node, [NotNullWhen(true)] out MongoElementRefExpression? elementRef)
    {
        if (SelfParam is not null && ReferenceEquals(node, SelfParam))
        {
            elementRef = new MongoElementRefExpression(MongoElementRefExpression.WholeRootDocumentPath, _entityType.ClrType);
            return true;
        }

        if (TryResolveOwnedReferenceNavigationPath(node, out var navPath, out var navigation, out var navIsOuter))
        {
            // An OUTER-scoped owned-nav path must decline, not resolve. The path this resolver returns is
            // relative to the OUTER entity's own document root, but MongoElementRefExpression renders
            // element-relative when an elementVariable is in scope ("$$this." + Path) — unlike
            // MongoOuterFieldExpression, which is root-anchored precisely for this reason but requires a
            // backing IProperty a navigation path does not have. Emitting the element ref anyway addressed a
            // field on the ELEMENT (e.g. "$$this.Address" on a Post, which has no Address at all), and the
            // NullSafe $ifNull then read that missing element as null — so `b.Posts.Any(p => b.Address == null)`
            // answered TRUE for every row regardless of the stored value. There is no root-anchored
            // element-ref node to emit instead today, so this shape belongs to driver-LINQ.
            if (navIsOuter)
            {
                elementRef = null;
                return false;
            }

            // nullSafe: true — unlike WholeRootDocumentPath, a real element path CAN be entirely missing from
            // the stored document (an unset owned single-reference nav), and $expr's $eq does not treat that
            // the same as an explicit null the way the query dialect's {field: null} does. See
            // MongoElementRefExpression.NullSafe's own remarks.
            elementRef = new MongoElementRefExpression(navPath, navigation.TargetEntityType.ClrType, nullSafe: true);
            return true;
        }

        elementRef = null;
        return false;
    }

    /// <summary>
    /// Resolves a nested member/navigation access chain whose LEAF hop is itself an owned/embedded
    /// single-reference (OwnsOne) navigation — e.g. <c>b.Address</c> or <c>b.Address.Recipient</c> — to its own
    /// dotted document path, mirroring <see cref="TryResolveOwnedFieldPath"/>'s walk but stopping ONE hop
    /// earlier: every hop, INCLUDING the leaf, must resolve to an embedded single-reference navigation, since
    /// the leaf here is the navigation itself, not a scalar property underneath it. Returns
    /// <see langword="false"/> for a collection navigation, a reference (non-embedded) navigation, or a single
    /// top-level hop off a non-parameter receiver.
    /// </summary>
    private bool TryResolveOwnedReferenceNavigationPath(
        Expression node, [NotNullWhen(true)] out string? path, [NotNullWhen(true)] out INavigation? navigation,
        out bool isOuter)
    {
        path = null;
        navigation = null;

        if (!TryBeginOwnedHopWalk(node, minimumHops: 1, out var names, out var scopeType, out isOuter))
            return false;

        // Unlike the two sibling resolvers, the LEAF here is itself a navigation, so EVERY hop — the last one
        // included — must be an embedded single reference. Walk them all, tracking the final hop's own
        // navigation, which is what this resolver reports.
        var segments = new List<string>(names.Count);
        INavigation? leafNavigation = null;
        foreach (var name in names)
        {
            var hop = scopeType.FindNavigation(name);
            if (hop is null || !hop.IsEmbedded() || hop.IsCollection)
                return false;

            var elementName = hop.TargetEntityType.GetContainingElementName();
            if (string.IsNullOrEmpty(elementName))
                return false;

            segments.Add(elementName);
            scopeType = hop.TargetEntityType;
            leafNavigation = hop;
        }

        // minimumHops: 1 above guarantees the loop ran at least once, so leafNavigation is set.
        navigation = leafNavigation!;
        path = string.Join(".", segments);
        return true;
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

        if (!TryBeginOwnedHopWalk(source, minimumHops: 1, out var names, out var scopeType, out isOuter))
            return false;

        // Every hop but the last must be an embedded single reference; the FINAL hop is the quantifier's own
        // source and must be an embedded COLLECTION navigation. That final-hop rule is the structural
        // protection against a mapped scalar property sharing a navigation's name — a scalar's receiver is
        // never a collection.
        var segments = new List<string>(names.Count);
        if (!TryWalkEmbeddedReferenceHops(names, names.Count - 1, ref scopeType, segments))
            return false;

        var collectionNavigation = scopeType.FindNavigation(names[^1]);
        if (collectionNavigation is null || !collectionNavigation.IsEmbedded() || !collectionNavigation.IsCollection)
            return false; // a primitive collection property, a reference nav, or a non-collection final hop

        var collectionElementName = collectionNavigation.TargetEntityType.GetContainingElementName();
        if (string.IsNullOrEmpty(collectionElementName))
            return false;

        segments.Add(collectionElementName);

        arrayPath = string.Join(".", segments);
        elementType = collectionNavigation.TargetEntityType;
        return true;
    }
}
