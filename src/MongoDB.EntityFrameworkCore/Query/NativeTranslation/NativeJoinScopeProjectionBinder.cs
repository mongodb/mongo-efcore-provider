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
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Attempts to populate the native <c>$project</c> slot for a wrapped <c>new {...}</c>/<c>MemberInit</c>
/// <c>Select</c> composed immediately after an eligible single-level <c>Join</c>/<c>LeftJoin</c>
/// (<see cref="MongoSelectDefinition.JoinScope"/>).
/// </summary>
/// <remarks>
/// <para>
/// An admitted leaf is one of: a scalar/computed value <see cref="NativeJoinScopeTranslator"/> can translate
/// (<c>x.Outer.Foo</c>, <c>x.Inner.Foo</c>, or a computed expression combining both), or a WHOLE-ENTITY leaf
/// (<c>x.Outer</c>/<c>x.Inner</c> verbatim — EF-444). The one combination deliberately NOT admitted is a
/// whole-entity leaf alongside a COMPUTED leaf: the whole projection then declines, because a computed leaf
/// has no document path for the whole-document fallback legs a whole-entity leaf forces — see the
/// "whole-entity leaf makes every SIBLING leaf's readability a precondition" paragraph below.
/// </para>
/// <para>
/// A whole-entity leaf (<c>x.Outer</c>/<c>x.Inner</c> verbatim) is asymmetric (EF-444) and BOTH sides now stage
/// (EF-444 Task 2 added the Inner arm). The OUTER leaf stages a <c>$$ROOT</c> reference
/// (<see cref="MongoElementRefExpression.WholeRootDocumentPath"/>) under the leaf's OWN alias, and the whole
/// projection proceeds — the bind side (<c>MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember</c>)
/// folds the join's own shaper into the selector body first, so the leaf arrives as the
/// <c>StructuralTypeShaperExpression</c> the join already built and gets rebound by index, rather than
/// mis-registered as a scalar alias read. The INNER leaf stages the SAME <c>$$ROOT</c>-analogue mechanism but
/// under a FIXED, self-referential alias — <see cref="MongoJoinScope.InnerPrefix"/> — used as BOTH the emitted
/// <c>$project</c> field name AND the <see cref="MongoElementRefExpression"/>'s path, NOT the member's own
/// alias. See the "Alias space" paragraph below for why this asymmetry is load-bearing and must not be
/// "corrected" back to the member alias.
/// </para>
/// <para>
/// On success: stages every leaf, then commits in one block — <c>Select.Projection</c> entries, the join's
/// already-built <see cref="JoinInfo.Lookup"/> (<c>AddLookup</c>), and
/// <see cref="MongoSelectDefinition.MarkReferenceIncludeConfirmed"/>. Nothing is mutated on any decline path,
/// so a rejected leaf can never leave a half-registered <c>$lookup</c> or a stray projection entry behind.
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
/// <b>On <see cref="NativeJoinScopeTranslator"/>'s documented RESIDUAL GAP.</b> That comment warns that the
/// first caller to translate the Inner side WITHOUT the <c>Where</c> arm's blanket
/// <c>ReferencesInnerScope</c> block — i.e. this binder — must either add a per-join identity check or
/// re-validate the scope against the actual join being bound. It is closed here structurally, by this binder's
/// call-site gate (<c>MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope</c>)
/// requiring <c>Joins.Count == 1</c>: a <c>JoinScope</c> is recorded only for the FIRST join on a select, and
/// every later join appends to that same <c>Joins</c> list on the same <c>MongoQueryExpression</c>, so "exactly
/// one join has ever been recorded here" and "the recorded scope describes that join" are one fact, making
/// <c>scope.InnerPrefix == joinInfo.Alias</c> hold by construction. The gap's own worked example —
/// <c>Join(a, b, …).Select(x =&gt; x.Outer).Join(c, d, …)</c> — reaches the trailing <c>Select</c> with
/// <c>Joins.Count == 2</c> and is declined by that gate. See the gate's own remarks for the full derivation;
/// it is stated in one place so the two arms cannot drift.
/// </para>
/// </remarks>
internal static class NativeJoinScopeProjectionBinder
{
    internal static bool TryBindProjection(
        MongoQueryExpression mongoQ, LambdaExpression selector, JoinInfo joinInfo)
    {
        if (mongoQ.Select.JoinScope is not { } scope
            || joinInfo.Lookup is not { } lookup
            || mongoQ.Select.Projection.Count > 0
            || selector.Parameters.Count != 1)
        {
            return false;
        }

        var rootParam = selector.Parameters[0];

        if (!TryReadMembers(selector.Body, out var members))
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
            // A whole-entity leaf (`x.Outer`/`x.Inner` verbatim). Recognized by the member's DECLARING TYPE
            // (IsTransparentIdentifierOuterOrInnerAccess), never by member name alone, so a joined entity that
            // happens to declare its own real "Outer"/"Inner" property is not mistaken for join-chain plumbing —
            // the same rule NativeJoinScopeTranslator's own splitter and ExpressionExtensionMethods document
            // every caller must stay in agreement on.
            //
            // EF-444: the OUTER leaf stages a $$ROOT reference (MongoElementRefExpression over
            // MongoElementRefExpression.WholeRootDocumentPath) under its OWN alias instead of declining — the
            // bind side (MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember) folds the join's
            // own shaper into the selector body first, so a whole-entity leaf arrives there as the
            // StructuralTypeShaperExpression the join already built and gets rebound by index, rather than
            // mis-registered as a scalar alias read. The INNER leaf (EF-444 Task 2) stages the same way but
            // under a FIXED, self-referential alias (scope.InnerPrefix, both as the $project field name and the
            // MongoElementRefExpression's path) — NOT the member's own alias — because the read side resolves
            // the inner entity's field name from the NAVIGATION, not the projection alias. See this class's own
            // "Alias space" remarks for the full reasoning; do not "fix" this back to the member alias.
            //
            // NativeJoinScopeTranslator would decline this leaf anyway (a bare scope leaf rewrites to the
            // synthetic scope parameter, which resolves to no field), so this check is about being explicit
            // and stable rather than about reachability.
            if (leafBody is MemberExpression member
                && ReferenceEquals(member.Expression, rootParam)
                && member.IsTransparentIdentifierOuterOrInnerAccess())
            {
                if (member.Member.Name == "Outer")
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
                    // Self-referential: alias AND path are both scope.InnerPrefix.
                    //
                    // The fixed alias is claimed on `seenAliases` EXPLICITLY here rather than only implicitly
                    // via the seed loop above. In normal operation this Add() returns FALSE — the alias is
                    // already seeded from mongoQ.Projection, where RebindInnerShaperToOuterQuery registered the
                    // inner entity's own EntityProjectionExpression under this very name — so its result is
                    // deliberately NOT a decline signal. What it buys is that this arm no longer depends
                    // silently on that three-file coupling (RebindInnerShaperToOuterQuery →
                    // EntityProjectionExpression.Name → the seed loop) for the ORDINARY-leaf arm below to
                    // decline a user member spelled exactly "_lookup_<Nav>".
                    seenAliases.Add(scope.InnerPrefix);

                    // Dedup so a duplicated Inner leaf (e.g. `new { a = r, b = r }`) stages this fixed alias
                    // only once — otherwise MongoQueryExpression/MongoPipelineFactory would hard-crash on a
                    // duplicate $project field name under Native/NativeOnly (an explicit DriverLinq builds no
                    // native pipeline, so it cannot crash there). Both members' bind-side AddToProjection calls
                    // dedup to the same index by expression equality regardless, so both read correctly.
                    //
                    // ASSERTED, not assumed (final-review finding): the already-staged entry must really be a
                    // previous Inner leaf of THIS projection. A bare `TrueForAll(p => p.Alias != InnerPrefix)`
                    // would treat ANY staged entry holding that alias as the dedup case and SILENTLY SKIP the
                    // Inner leaf — a dropped value, not a decline — were the seeding coupling above ever to
                    // break and let a user member named "_lookup_<Nav>" stage first. Declining converts that
                    // latent silent drop into an explicit, visible fallback.
                    var existingIndex = staged.FindIndex(p => p.Alias == scope.InnerPrefix);
                    if (existingIndex < 0)
                    {
                        staged.Add(new MongoProjection(
                            scope.InnerPrefix,
                            new MongoElementRefExpression(scope.InnerPrefix, scope.InnerEntityType.ClrType)));
                    }
                    else if (staged[existingIndex].Expression is not MongoElementRefExpression existingRef
                             || existingRef.Path != scope.InnerPrefix)
                    {
                        return false;
                    }
                }

                continue;
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
        if (staged.Exists(p => p.Expression is MongoElementRefExpression)
            && staged.Exists(p => p.Expression is not (MongoElementRefExpression or MongoFieldExpression)))
        {
            return false;
        }

        foreach (var projection in staged)
        {
            mongoQ.Select.AddProjection(projection);
        }

        mongoQ.AddLookup(lookup);
        mongoQ.Select.MarkReferenceIncludeConfirmed();
        return true;
    }

    /// <summary>
    /// Splits an anonymous-type / DTO construction into its (member name, value expression) pairs.
    /// </summary>
    /// <remarks>
    /// Deliberately a local copy of the same two-case shape <c>NativeProjectionBinder</c> and
    /// <c>NativeGroupByBinder</c> each parse: theirs are inlined into their own leaf-translation loops
    /// (interleaved with alias derivation, pending-lookup staging and array-leaf bookkeeping this binder has no
    /// analogue for), so there is no existing accessible helper to call. Kept to the same two admitted shapes —
    /// a <see cref="NewExpression"/> carrying <c>Members</c>, or a <see cref="MemberInitExpression"/> over a
    /// parameterless constructor with <see cref="MemberAssignment"/> bindings only.
    /// </remarks>
    private static bool TryReadMembers(
        Expression body, out IReadOnlyList<(string Alias, Expression Leaf)> members)
    {
        var list = new List<(string, Expression)>();
        members = list;

        switch (body)
        {
            case NewExpression newExpression
                when newExpression.Members != null
                     && newExpression.Members.Count == newExpression.Arguments.Count
                     && newExpression.Arguments.Count > 0:
                for (var i = 0; i < newExpression.Arguments.Count; i++)
                {
                    list.Add((newExpression.Members[i].Name, newExpression.Arguments[i]));
                }

                return true;

            case MemberInitExpression memberInit
                when memberInit.NewExpression.Arguments.Count == 0
                     && memberInit.Bindings.Count > 0:
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                    {
                        return false;
                    }

                    list.Add((binding.Member.Name, assignment.Expression));
                }

                return true;

            default:
                return false;
        }
    }
}
