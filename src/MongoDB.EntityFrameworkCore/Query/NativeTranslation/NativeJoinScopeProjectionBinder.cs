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
using System.Diagnostics;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Attempts to populate the native <c>$project</c> slot for a wrapped <c>new {...}</c>/<c>MemberInit</c>
/// <c>Select</c> composed immediately after an eligible <c>Join</c>/<c>LeftJoin</c> — a single level, or (since
/// the native-chained-join-scope plan, Task 6) a CHAIN of them — (<see cref="MongoSelectDefinition.JoinScope"/>).
/// </summary>
/// <remarks>
/// <para>
/// For a DEPTH-1 scope (<c>scope.Levels.Count == 1</c>, unchanged from before Task 6): an admitted leaf is one
/// of: a scalar/computed value <see cref="NativeJoinScopeTranslator"/> can translate (<c>x.Outer.Foo</c>,
/// <c>x.Inner.Foo</c>, or a computed expression combining both), or a WHOLE-ENTITY leaf (<c>x.Outer</c>/
/// <c>x.Inner</c> verbatim — EF-444). The one combination deliberately NOT admitted is a whole-entity leaf
/// alongside a COMPUTED leaf: the whole projection then declines, because a computed leaf has no document path
/// for the whole-document fallback legs a whole-entity leaf forces — see the "whole-entity leaf makes every
/// SIBLING leaf's readability a precondition" paragraph below.
/// </para>
/// <para>
/// For a CHAIN (<c>scope.Levels.Count &gt; 1</c>, Task 6): every leaf MUST be a whole-entity leaf naming some
/// scope in the chain (the root, or any level's Inner side) — a scalar/computed leaf declines the WHOLE
/// projection outright, rather than being attempted through <see cref="NativeJoinScopeTranslator.TryTranslateValue"/>,
/// because that method's own flat, depth-1-only shape check can — for one specific coincidental shape — pass
/// while actually resolving against the WRONG level (see the "chain-only" comment on that decline in
/// <see cref="TryBindProjection"/> for the worked example, and <c>NativeJoinScopeTranslator</c>'s own
/// documented RESIDUAL GAP). This is a strict widening at Levels.Count == 1 (identical to before) and a
/// stricter-than-depth-1 restriction at Levels.Count &gt; 1 (only whole-entity leaves, no scalars/computed at
/// all) — see docs/superpowers/specs/2026-09-07-native-chained-join-scope-design.md, Component 6's "Out of
/// scope" list.
/// </para>
/// <para>
/// A whole-entity leaf (<c>x.Outer</c>/<c>x.Inner</c> verbatim) is asymmetric (EF-444) and BOTH sides now stage
/// (EF-444 Task 2 added the Inner arm). The OUTER leaf stages a <c>$$ROOT</c> reference
/// (<see cref="MongoElementRefExpression.WholeRootDocumentPath"/>) under the leaf's OWN alias, and the whole
/// projection proceeds — the bind side (<c>MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember</c>)
/// folds the join's own shaper into the selector body first, so the leaf arrives as the
/// <c>StructuralTypeShaperExpression</c> the join already built and gets rebound by index, rather than
/// mis-registered as a scalar alias read. The INNER leaf stages the SAME <c>$$ROOT</c>-analogue mechanism but
/// under a FIXED, self-referential alias — <see cref="MongoJoinScopeLevel.InnerPrefix"/> — used as BOTH the emitted
/// <c>$project</c> field name AND the <see cref="MongoElementRefExpression"/>'s path, NOT the member's own
/// alias. See the "Alias space" paragraph below for why this asymmetry is load-bearing and must not be
/// "corrected" back to the member alias.
/// </para>
/// <para>
/// On success: stages every leaf, then commits in one block — <c>Select.Projection</c> entries, EVERY level's
/// already-built <c>Lookup</c> (<c>AddLookup</c>, once per <see cref="MongoJoinScopeLevel"/> — for a chain this
/// is redundant-but-harmless, since <c>TranslateJoinCore</c> already unconditionally registered every level's
/// lookup the moment <c>Joins.Count &gt; 1</c>), and <see cref="MongoSelectDefinition.MarkReferenceIncludeConfirmed"/>
/// (also once per level, matching the once-per-join <c>MarkSawCandidateReferenceIncludeJoin</c> call recorded
/// at join-registration time). Nothing is mutated on any decline path, so a rejected leaf can never leave a
/// half-registered <c>$lookup</c> or a stray projection entry behind.
/// </para>
/// <para>
/// <b>Alias space.</b> Every ORDINARY leaf (a scalar/computed value, or a whole-entity OUTER leaf) is emitted
/// under its own member name, which is exactly the <c>ProjectionMember</c> the shaper side derives for the same
/// leaf (<c>MongoProjectionBindingExpressionVisitor.VisitNew</c> pushes <c>newExpression.Members[i]</c>), so the
/// emit side and the read side agree by construction — the same rule <c>NativeProjectionBinder</c>'s
/// wrapped-leaf path follows. Aliases are deduped case-INSENSITIVELY, because
/// <c>MongoQueryExpression.AddToProjection</c> disambiguates them that way: two members differing only by case
/// would have the shaper read a disambiguated alias the <c>$project</c> never emitted.
/// </para>
/// <para>
/// <b>The whole-entity INNER leaf is the one deliberate exception to that rule, and future editors must not
/// "fix" it back to the member alias.</b> Its emitted <c>$project</c> field is fixed at
/// <c>scope.InnerPrefix</c> (e.g. <c>"_lookup_Orders"</c>) regardless of what the user named the member (e.g.
/// <c>r</c> in <c>new { o, r }</c>), because the READ side does not resolve the inner entity's field name from
/// the projection alias at all —
/// <c>MongoProjectionBindingRemovingExpressionVisitor.VisitBinary</c>'s cross-collection arm OVERWRITES
/// whatever alias was staged with <c>GetCrossCollectionFieldName(accessExpression)</c> (the navigation's own
/// <c>_lookup_&lt;Nav&gt;</c> name), discarding <c>projection.Alias</c> entirely. Staging the Inner leaf under
/// the member's own alias instead would silently desynchronize the emitted <c>$project</c> field from what the
/// shaper actually reads — a dropped/wrong value, not a compile error or an obvious test failure. A duplicated
/// Inner leaf in one projection (e.g. <c>new { a = r, b = r }</c>) would also collide on this same fixed alias
/// and crash pipeline construction (<c>InvalidOperationException: Duplicate element name</c>) under
/// <c>MongoQueryMode.Native</c>/<c>NativeOnly</c> were it not for the dedup guard below — an explicit
/// <c>MongoQueryMode.DriverLinq</c> never builds a native pipeline at all, so the crash cannot occur there;
/// that leg instead takes the stripped whole-document read with the alias staged once and both members
/// index-bound to it. Both members' bind-side <c>AddToProjection</c> calls already dedup to the same index by
/// expression equality, so emitting the field once is sufficient for both to read correctly in either leg.
/// </para>
/// <para>
/// <b>A whole-entity leaf makes every SIBLING leaf's readability a precondition (EF-444 Task 4).</b> Both
/// fallback legs for such a projection — an explicit <c>MongoQueryMode.DriverLinq</c>, and a translate-time
/// <c>Route == Projection</c> followed by a mid-compile <c>TryBuildNativeFactory</c> decline — strip the
/// pushed-down <c>Select</c> and shape WHOLE, un-projected documents. A FIELD leaf survives that: the mixed
/// shaper reads it by its own root-relative path (<c>MongoFieldExpression.ElementName</c>), so a renamed alias
/// or a joined dotted path both resolve. A COMPUTED leaf does not — it has no document path at all — so the
/// whole projection DECLINES when the two are mixed, rather than emitting a <c>$project</c> whose fallback read
/// is impossible. See the guard just before the commit block, and
/// <c>MongoShapedQueryCompilingExpressionVisitor.HasJoinScopeInnerEntityProjectionLeaf</c> for the leg it
/// protects.
/// </para>
/// <para>
/// <b>Non-default serialization needs no guard here.</b>
/// <see cref="NativeJoinScopeTranslator.TryTranslateValue"/> routes through
/// <c>MongoExpressionTranslator.TryTranslateValue</c>, which already applies
/// <c>AllFieldsDefaultSerialized</c> to the whole translated subtree — a value-converted or
/// non-default-<c>BsonRepresentation</c> field declines there, before this binder sees it.
/// </para>
/// <para>
/// <b>On <see cref="NativeJoinScopeTranslator"/>'s documented RESIDUAL GAP (updated for Task 6).</b> That
/// comment warns that the first caller to translate the Inner side WITHOUT the <c>Where</c> arm's blanket
/// <c>ReferencesInnerScope</c> block — i.e. this binder — must either add a per-join identity check or
/// re-validate the scope against the actual join being bound. Before Task 6 it was closed structurally by this
/// binder's call-site gate requiring <c>Joins.Count == 1</c> outright. Task 6's gate widening
/// (<c>MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope</c>, now
/// <c>scope.Levels.Count == Joins.Count</c>) reopens the gap's own worked example — a chained second join whose
/// OWN flat <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c> coincidentally matches the recorded scope's types
/// (<c>Join(a, b, …).Select(x =&gt; x.Outer).Join(c, d, …)</c>) — as far as THAT gate is concerned (both joins
/// individually eligible ⇒ the chain is rebuilt to cover both). It stays closed one layer down, HERE: this
/// binder resolves a whole-entity leaf via <c>MongoTransparentScopeResolver.TryResolveScopeDepth</c>, which
/// walks the actual member-NAME chain (never compares CLR types), and — the load-bearing half — a chain
/// (<c>Levels.Count &gt; 1</c>) never falls through to the flat, type-comparing
/// <c>NativeJoinScopeTranslator.TryTranslateValue</c> the gap warns about at all, for ANY leaf that doesn't
/// resolve as whole-entity. See <c>NativeJoinScopeProjectionBinderTests
/// .Declines_a_second_chained_join_rather_than_reusing_the_first_joins_scope</c>, whose trailing selector's
/// leaves are scalar (not whole-entity) and so still declines here, unchanged, proving the gap's worked example
/// remains closed after the widening.
/// </para>
/// </remarks>
internal static class NativeJoinScopeProjectionBinder
{
    internal static bool TryBindProjection(
        MongoQueryExpression mongoQ, LambdaExpression selector, JoinInfo joinInfo)
    {
        if (mongoQ.Select.JoinScope is not { } scope
            || joinInfo.Lookup is null
            || mongoQ.Select.Projection.Count > 0
            || selector.Parameters.Count != 1)
        {
            return false;
        }

        var rootParam = selector.Parameters[0];

        if (!selector.Body.TryGetProjectionMembers(out var members))
        {
            return false;
        }

        var staged = new List<MongoProjection>();

        // Seeded with the aliases ALREADY on the query expression, not just the ones staged here. A join query
        // never reaches this binder with an empty MongoQueryExpression.Projection: RebindInnerShaperToOuterQuery
        // registered the inner entity's own EntityProjectionExpression at join time, under an alias derived from
        // the "_lookup_<Nav>" access-expression name. AddToProjection uniquifies case-INSENSITIVELY by appending
        // a counter, so a user member literally named to collide with that internal alias would have the SHAPER
        // read the renamed alias while the emitted $project still carries the original — a silent dropped value,
        // not an error. Declining the whole projection is the only safe answer: the emitted alias is fixed by
        // the member name (that agreement is what makes the shaper correct at all), so there is nothing to
        // renegotiate. Narrow by construction — it takes a DTO/anonymous member spelled exactly like the
        // provider's internal lookup alias — but free to close, and the failure mode if left open is silent.
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in mongoQ.Projection)
        {
            if (existing.Alias is { } existingAlias)
            {
                seenAliases.Add(existingAlias);
            }
        }

        foreach (var (alias, leafBody) in members)
        {
            // A whole-entity leaf naming ANY scope in the chain — the root (scope index 0) or any level's
            // Inner side (index k, 1 <= k <= scope.Levels.Count). Generalized (native-chained-join-scope plan,
            // Task 6) from the old flat, single-hop `IsTransparentIdentifierOuterOrInnerAccess` check to
            // MongoTransparentScopeResolver.TryResolveScopeDepth, the SAME chained-scope walker
            // NativeSelectManyBinder already uses — resolution is still entirely by parameter IDENTITY
            // (rootParam) and member NAME-CHAIN SHAPE (a pure run of "Outer" hops, optionally ending in one
            // "Inner"), never by a single member's declaring type alone, but the shape it recognizes now
            // spans the whole chain instead of one hop. A leaf whose body is not EXACTLY one of these two
            // shapes (a computed/mixed leaf, a leaf reaching further through a scope, or one spanning more
            // than one scope) fails to resolve and falls through to the ordinary scalar/computed arm below.
            //
            // EF-444: the ROOT leaf (scopeIndex == 0) stages a $$ROOT reference (MongoElementRefExpression
            // over MongoElementRefExpression.WholeRootDocumentPath) under its OWN alias instead of declining —
            // the bind side (MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember) folds the
            // join's own shaper into the selector body first, so a whole-entity leaf arrives there as the
            // StructuralTypeShaperExpression the join already built and gets rebound by index, rather than
            // mis-registered as a scalar alias read. An INNER leaf at level k (EF-444 Task 2, generalized to
            // any k here) stages the same way but under a FIXED, self-referential alias
            // (scope.Levels[k-1].InnerPrefix, both as the $project field name and the
            // MongoElementRefExpression's path) — NOT the member's own alias — because the read side resolves
            // the inner entity's field name from the NAVIGATION, not the projection alias. See this class's own
            // "Alias space" remarks for the full reasoning; do not "fix" this back to the member alias.
            //
            // NativeJoinScopeTranslator.TryTranslateValue (the ordinary-leaf arm below) would decline this leaf
            // anyway for a depth-1 scope (a bare scope leaf rewrites to the synthetic scope parameter, which
            // resolves to no field) and is never even attempted for a depth-&gt;1 chain (see the comment on that
            // arm below) — so this check is about being explicit and stable rather than about reachability.
            if (MongoTransparentScopeResolver.TryResolveScopeDepth(
                    leafBody, rootParam, hopNames: ["Outer", "Inner"], sourceCount: scope.Levels.Count, out var scopeIndex))
            {
                if (scopeIndex == 0)
                {
                    if (!seenAliases.Add(alias))
                    {
                        return false;
                    }

                    staged.Add(new MongoProjection(
                        alias,
                        new MongoElementRefExpression(
                            MongoElementRefExpression.WholeRootDocumentPath, mongoQ.CollectionExpression.EntityType.ClrType)));
                }
                else
                {
                    var level = scope.Levels[scopeIndex - 1];

                    // Self-referential: alias AND path are both level.InnerPrefix.
                    //
                    // The fixed alias is claimed on `seenAliases` EXPLICITLY here rather than only implicitly
                    // via the seed loop above. In normal operation this Add() returns FALSE — the alias is
                    // already seeded from mongoQ.Projection, where RebindInnerShaperToOuterQuery registered the
                    // inner entity's own EntityProjectionExpression under this very name — so its result is
                    // deliberately NOT a decline signal. What it buys is that this arm no longer depends
                    // silently on that three-file coupling (RebindInnerShaperToOuterQuery →
                    // EntityProjectionExpression.Name → the seed loop) for the ORDINARY-leaf arm below to
                    // decline a user member spelled exactly "_lookup_<Nav>".
                    seenAliases.Add(level.InnerPrefix);

                    // Dedup so a duplicated Inner leaf at the SAME level (e.g. `new { a = r, b = r }`) stages
                    // this fixed alias only once — otherwise MongoQueryExpression/MongoPipelineFactory would
                    // hard-crash on a duplicate $project field name under Native/NativeOnly (an explicit
                    // DriverLinq builds no native pipeline, so it cannot crash there). Both members' bind-side
                    // AddToProjection calls dedup to the same index by expression equality regardless, so both
                    // read correctly. This is a per-LEVEL dedup key (level.InnerPrefix), not per-leaf — two
                    // leaves naming the SAME level dedupe here; two leaves naming DIFFERENT levels each stage
                    // (and later confirm) independently, which is exactly the chain-of-N generalization.
                    //
                    // ASSERTED, not assumed (final-review finding, depth-1): the already-staged entry must
                    // really be a previous Inner leaf of THIS level. A bare `TrueForAll(p => p.Alias !=
                    // level.InnerPrefix)` would treat ANY staged entry holding that alias as the dedup case and
                    // SILENTLY SKIP the Inner leaf — a dropped value, not a decline — were the seeding coupling
                    // above ever to break and let a user member named "_lookup_<Nav>" stage first. Declining
                    // converts that latent silent drop into an explicit, visible fallback.
                    var existingIndex = staged.FindIndex(p => p.Alias == level.InnerPrefix);
                    if (existingIndex < 0)
                    {
                        staged.Add(new MongoProjection(
                            level.InnerPrefix,
                            new MongoElementRefExpression(level.InnerPrefix, level.InnerEntityType.ClrType)));
                    }
                    else if (staged[existingIndex].Expression is not MongoElementRefExpression existingRef
                             || existingRef.Path != level.InnerPrefix)
                    {
                        return false;
                    }
                }

                continue;
            }

            // A NESTED wrapped leaf (`CustomerId = new { Id = o.Customer!.CustomerID }`) — one level of
            // nesting only (native-join-scope-nested-projection ticket). Declines the WHOLE outer leaf (not
            // just this member) on any inner shape this doesn't recognize, exactly as
            // NativeProjectionBinder.TryGetDocumentConstructionLeaf does for its own plain-root nested
            // leaves — this is a SEPARATE recognizer building the same MongoDocumentConstructionExpression
            // node, not a relaxation of that one's dotted-field decline.
            //
            // EF10-ONLY IN PRACTICE, and not because of anything in this file. On EF8/EF9 an OPTIONAL
            // reference navigation (the shape a reference Include produces) is lowered by EF's nav-expansion
            // onto EF's own internal LeftJoin shim (MongoQueryableMethodTranslatingExpressionVisitor
            // .Ef8Ef9LeftJoinMethod), and NativeSlotPopulator.PopulateNativeSlots' candidate-join arm only
            // matches QueryableMethods.{Join,GroupJoin} plus — under `#if !EF8 && !EF9` — QueryableMethods
            // .LeftJoin, which does not exist before EF10. The shim therefore falls through to that method's
            // catch-all and calls MarkNotNativelyRepresentable() before ANY Select-side binder runs, so
            // TryBindProjection is never even reached (MEASURED: HasUnsupportedOperator is already true when
            // TranslateSelect's wrapped arm consults IsSingleEligibleNativeJoinScope on EF9, false on EF10).
            // That gap is family-wide, not nesting-specific — on EF8/EF9 NO wrapped projection over an
            // optional-reference join binds natively, including the flat `new { o.Total, o.Customer.Name }`
            // shape that predates this arm. A REQUIRED reference navigation lowers to QueryableMethods.Join
            // instead and does go native on all three EF versions, this arm included. Consequence for tests:
            // a functional/spec test asserting NativeOnly SUCCESS for an optional-reference nested projection
            // must be `#if !EF8 && !EF9`-guarded (or assert the throw), and the MQL baselines differ.
            if (scope.Levels.Count == 1
                && leafBody.TryGetProjectionMembers(out var nestedMembers))
            {
                var translatedNestedMembers = new List<(string, MongoExpression)>();
                var seenNestedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var declined = false;

                foreach (var (nestedMemberName, nestedValue) in nestedMembers)
                {
                    // MUST be a MongoFieldExpression, NOT merely "anything TryTranslateValue accepts"
                    // (final-review Critical 1). The shared read side —
                    // MongoProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMemberTyped —
                    // hard-casts each member's staged value to MongoFieldExpression, so staging any other node
                    // kind here is an InvalidCastException at QUERY-COMPILE time, in the DEFAULT Native mode,
                    // outside every TryBuildNativeFactory decline path: a hard crash for a shape that works
                    // under MongoQueryMode.DriverLinq. Two node kinds TryTranslateValue really does return for
                    // an admissible-looking nested member reach that cast: a MongoBinaryExpression for a
                    // COMPUTED member (`Combo = o.OrderNo + o.Customer!.Rank`), and a MongoOuterFieldExpression
                    // for an OUTER-sourced member (`Copy = new { N = o.OrderNo }` — the two-scope translator
                    // resolves outer-rooted access to the root-anchored sibling type, see
                    // MongoExpressionTranslator's `operandIsOuter` arm). Both now decline the whole outer leaf
                    // and fall back, exactly as before this arm existed. So the shapes this arm actually
                    // ACCEPTS are precisely the INNER-sourced plain field members — whose ElementName is the
                    // dotted "<InnerPrefix>.<element>" path both read sides know how to walk — which is the
                    // motivating shape and the only one with end-to-end coverage.
                    //
                    // This is the same node-kind requirement the sibling recognizer
                    // NativeProjectionBinder.TryGetDocumentConstructionLeaf enforces via
                    // MongoExpressionTranslator.TryTranslateField (which is MongoFieldExpression-typed by
                    // signature); stating it here as an explicit type test is the closest this arm can get to
                    // that, since NativeJoinScopeTranslator has no field-typed entry point.
                    //
                    // DELIBERATELY NOT applying that sibling's two further conjuncts:
                    //  * `!field.ElementName.Contains('.')` — a dotted ElementName is the NORMAL, required
                    //    shape here (the Inner side lives under the join's "_lookup_<Nav>" sub-document), and
                    //    both read legs walk it segment-by-segment: the native leg reads [alias, memberName]
                    //    out of the $project's own output, and the mixed/fallback leg explicitly splits a
                    //    dotted ElementName (MongoMixedProjectionBindingRemovingExpressionVisitor
                    //    .ReadDocumentConstructionMember). The sibling declines dotted fields because ITS
                    //    members are owned-hop paths with no such natural read, not because dots are unsafe.
                    //  * NativeGroupByBinder.HasDefaultKeySerialization(field.Property) (EF-447) — it would be
                    //    DEAD CODE here, for two independent reasons. (1) MEASURED: the join-scope value
                    //    translator already declines a value-converted member outright, and does so for a
                    //    FLAT join-scope leaf exactly as for a nested one — an ordinary
                    //    `new { C = x.Inner.ConvertedProperty }` over the same join is NativeRoute.Fallback
                    //    while the unconverted sibling is NativeRoute.Projection — so no property the guard
                    //    would reject can reach this loop at all. That is pre-existing behavior neither
                    //    introduced nor widened here; it is pinned by NativeJoinScopeNestedProjectionTests
                    //    .Nested_projection_with_value_converted_member_falls_back (functional), so a future
                    //    widening of the translator reddens rather than silently inheriting this reasoning.
                    //    (2) Even if one did reach here, the guard's actual hazard — a value read back through
                    //    a GENERIC CLR-TYPE serializer with no backing IProperty, the way a $group _id is —
                    //    does not exist on this node: every read of it, on BOTH legs, goes through
                    //    BsonBinding.CreateGetPropertyValueAtPath, which is PROPERTY-aware (it is handed
                    //    field.Property and reads through that property's own serializer/nullability).
                    if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, nestedValue, out var nestedLeaf)
                        || nestedLeaf is not MongoFieldExpression
                        || !seenNestedMembers.Add(nestedMemberName))
                    {
                        declined = true;
                        break;
                    }

                    translatedNestedMembers.Add((nestedMemberName, nestedLeaf));
                }

                if (declined)
                {
                    return false;
                }

                if (!seenAliases.Add(alias))
                {
                    return false;
                }

                staged.Add(new MongoProjection(
                    alias, new MongoDocumentConstructionExpression(leafBody, translatedNestedMembers)));
                continue;
            }

            // ORDINARY (scalar/computed) leaf. Only attempted for a DEPTH-1 scope, unchanged from before this
            // task — NativeJoinScopeTranslator.TryTranslateValue's own flat-shape check (TryTranslateCore) only
            // ever resolves against scope.Levels[0], and its documented RESIDUAL GAP is precisely that a
            // CHAINED join whose own flat TransparentIdentifier<TOuter,TInner> coincidentally matches the
            // recorded scope's Outer/Inner CLR types (e.g. two joins re-targeting the same entity type, with an
            // intermediate confirming Select flattening the first back down to a plain entity — see
            // MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope's own remarks)
            // can pass that check while actually belonging to a LATER level — misresolving the leaf against the
            // WRONG level's $lookup alias, silently. TryResolveScopeDepth above safely and structurally
            // recognizes a whole-entity leaf at ANY level without this hazard (it walks the ACTUAL member-name
            // chain rather than comparing CLR types), which is why it is tried first and unconditionally; a
            // scalar/computed leaf has no such safe generalization available in this ticket's scope (see the
            // design doc's explicit "Out of scope" listing), so for Levels.Count > 1 this arm declines the
            // WHOLE projection outright rather than risk the flat-shape translator's known-unsafe fallback.
            if (scope.Levels.Count > 1)
            {
                return false;
            }

            if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, leafBody, out var computedLeaf))
            {
                return false; // one untranslatable leaf declines the whole projection — no partial commit
            }

            if (!seenAliases.Add(alias))
            {
                return false;
            }

            staged.Add(new MongoProjection(alias, computedLeaf));
        }

        // WHOLE-ENTITY LEAF ⇒ EVERY SIBLING MUST BE WHOLE-DOCUMENT-READABLE (EF-444 Task 4).
        //
        // A whole-entity leaf forces both fallback legs — an explicit MongoQueryMode.DriverLinq, and a
        // translate-time Route == Projection followed by a mid-compile TryBuildNativeFactory decline — to strip
        // the pushed-down Select and shape WHOLE, un-projected documents (see
        // MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery and its createFallbackBindingRemover
        // arm). Every sibling leaf must therefore be readable out of such a document.
        //
        // A FIELD leaf always is: MongoMixedProjectionBindingRemovingExpressionVisitor
        // .TryBindNativeFieldLeafAsDocumentPath reads it by its own root-relative path
        // (MongoFieldExpression.ElementName), so a renamed alias (`new { N = o.Name, r }`) or a path that is not
        // an alias at all (`new { o, r.Total }` → "_lookup_Orders.Total") both resolve correctly. A COMPUTED
        // leaf has no such path — it exists only as a value the $project stage would have materialised — so
        // reading it off a whole document is impossible: MEASURED as
        // `Document element 'X' is missing but required` for `new { o, X = r.Total * 2 }` under DriverLinq.
        //
        // Declining the whole projection is the answer, not a partial commit: the shape then routes exactly as
        // it did before EF-444 (Route == Fallback → the mixed shaper over EF's own ProjectionMapping), which is
        // measurably correct in every mode. This is the same rule NativeProjectionBinder.IsWholeDocumentReadableLeaf
        // applies for an array leaf's siblings, and for the same reason.
        // MongoOuterFieldExpression is included alongside MongoFieldExpression: a join scope's OUTER-side
        // scalar leaf resolves as one via NativeJoinScopeTranslator's shared TranslateOperand path (see that
        // method's own remarks in MongoExpressionTranslator.cs), and it is exactly as document-root-relative/
        // whole-document-readable as MongoFieldExpression — MongoMixedProjectionBindingRemovingExpressionVisitor
        // .TryBindNativeFieldLeafAsDocumentPath reads both the same way. Treating it as a NON-readable sibling
        // here would decline a shape (a scalar Outer leaf mixed with a whole-entity leaf) that is in fact fine.
        if (staged.Exists(p => p.Expression is MongoElementRefExpression)
            && staged.Exists(p => p.Expression is not (MongoElementRefExpression or MongoFieldExpression or MongoOuterFieldExpression)))
        {
            return false;
        }

        foreach (var projection in staged)
        {
            mongoQ.Select.AddProjection(projection);
        }

        ConfirmEntireChain(mongoQ, scope);
        return true;
    }

    /// <summary>
    /// Registers every level's own <c>$lookup</c> and confirms every level's candidate join, exactly once
    /// each, unconditionally over <paramref name="scope"/>.Levels/<c>mongoQ.Joins</c> by matching index — NOT
    /// gated on whether some leaf happened to name that level as a whole entity. This is a deliberate
    /// generalization of depth-1's own existing unconditional behavior (<c>Levels.Count == 1</c>: the old
    /// inline code always called <c>AddLookup(joinInfo.Lookup)</c> + <c>MarkReferenceIncludeConfirmed()</c>
    /// exactly once on any successful bind, including an Outer-scalar-only projection that never references
    /// the Inner side at all — e.g. <c>new { o.Name }</c> alone — because reaching the caller's success path
    /// IS that select's dedicated confirming operator for that ONE join, full stop), not a narrower "only the
    /// levels a leaf actually named" rule: gating on "named" would leave a level's
    /// <c>MarkReferenceIncludeConfirmed()</c> call never made whenever a projection doesn't happen to
    /// reference it by a whole-entity leaf (a plain scalar/Outer-only leaf touching it, or no leaf touching it
    /// at all), permanently tripping <see cref="MongoSelectDefinition.HasUnconfirmedCandidateJoin"/>'s strict
    /// candidate/confirmed COUNT equality (one <c>MarkSawCandidateReferenceIncludeJoin</c> was recorded per
    /// join at <c>TranslateJoinCore</c> time, unconditionally, so every one of them needs exactly one matching
    /// confirmation to ever route native) — and, far worse for the depth-1 case specifically, gating
    /// <c>AddLookup</c> on "named" would leave that JOIN'S OWN <c>$lookup</c> NEVER REGISTERED AT ALL whenever
    /// no leaf is whole-entity (depth-1 defers ALL lookup registration to this exact call; unlike a chain,
    /// nothing else registers it first), which would silently omit the <c>$lookup</c> stage entirely rather
    /// than just declining. For a genuine chain (<c>Joins.Count &gt; 1</c>) this loop's <c>AddLookup</c> calls
    /// are redundant-but-harmless (already unconditionally registered by <c>TranslateJoinCore</c>'s own
    /// multi-join flattening the moment the second join was seen — see
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope</c>'s remarks) —
    /// <c>AddLookup</c> dedupes by alias — but the <c>MarkReferenceIncludeConfirmed()</c> calls remain
    /// load-bearing at every depth, since nothing else ever calls that one.
    /// <para>
    /// <b>Second call site (native-chained-join-scope plan, Task 6 final round).</b> Also called directly by
    /// <see cref="NativeCardinalityBinder.TryBindAggregate"/> for a scalar aggregate that reaches its own
    /// unconditional success point with an eligible, still-unconfirmed <see cref="MongoJoinScope"/> and NO
    /// trailing Select at all in the tree — see that method's own remarks (and
    /// <c>IsSingleEligibleNativeJoinScope</c>'s) for why such a Select-less shape is real and reachable
    /// (<c>Join(…).Join(…).Where(…).OrderBy(…).Any()</c>), not a theoretical concern.
    /// </para>
    /// </summary>
    internal static void ConfirmEntireChain(MongoQueryExpression mongoQ, MongoJoinScope scope)
    {
        // Defense-in-depth (final-review fix, I1/M1): every caller of this method reaches it only after
        // IsSingleEligibleNativeJoinScope has confirmed scope.Levels.Count == mongoQ.Joins.Count AND every
        // join's Lookup is non-null — so this bound should always hold. Asserting it here means a future
        // caller that skips (or weakens) that gate fails loudly instead of silently under-registering a
        // $lookup while still marking that level's reference-Include confirmed.
        Debug.Assert(
            scope.Levels.Count == mongoQ.Joins.Count,
            "ConfirmEntireChain requires one MongoJoinScopeLevel per join on mongoQ.Joins.");

        for (var i = 0; i < scope.Levels.Count; i++)
        {
            if (mongoQ.Joins[i].Lookup is { } levelLookup)
            {
                mongoQ.AddLookup(levelLookup);
            }

            mongoQ.Select.MarkReferenceIncludeConfirmed();
        }

        mongoQ.Select.MarkJoinLookupConfirmed();
    }
}
