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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates the native <c>$project</c> slot for a wrapped <c>new {...}</c>/<c>MemberInit</c> <c>Select</c>
/// composed directly after an eligible <c>Join</c>/<c>LeftJoin</c> or chain of them
/// (<see cref="MongoSelectDefinition.JoinScope"/>).
/// </summary>
/// <remarks>
/// <para>
/// Admitted leaves: a whole-entity leaf naming any scope in the chain (<c>x.Outer</c>/<c>x.Inner</c>), or a
/// scalar/computed leaf. At depth 1 the latter goes through <see cref="NativeJoinScopeTranslator.TryTranslateValue"/>;
/// for a chain it must resolve to exactly one scope via <see cref="NativeJoinScopeTranslator.TryTranslateSingleScope"/>,
/// because <c>TryTranslateValue</c>'s flat CLR-type check can match the wrong level when a chained join reuses
/// a target type (its documented residual gap). A leaf spanning several scopes declines the whole projection.
/// </para>
/// <para>
/// A whole-entity Outer leaf stages a <c>$$ROOT</c> reference (<see cref="MongoElementRefExpression.WholeRootDocumentPath"/>)
/// under its own alias; <c>BindResultMember</c> folds the join's shaper into the selector first so the leaf is
/// rebound by index. A whole-entity Inner leaf stages under the fixed alias <see cref="MongoJoinScopeLevel.InnerPrefix"/>,
/// not the member alias — see "Alias space".
/// </para>
/// <para>
/// Stages every leaf, then commits in one block (projections, every level's lookup, one confirmation per level
/// via <see cref="ConfirmEntireChain"/>). Nothing is mutated on a decline path.
/// </para>
/// <para>
/// <b>Alias space.</b> Ordinary leaves are emitted under their member name, which is the <c>ProjectionMember</c>
/// the shaper derives, so emit and read agree by construction. Aliases are deduped case-insensitively because
/// <c>MongoQueryExpression.AddToProjection</c> disambiguates that way. The Inner leaf is the deliberate exception:
/// <c>MongoProjectionBindingRemovingExpressionVisitor.VisitBinary</c>'s cross-collection arm reads the
/// navigation's <c>_lookup_&lt;Nav&gt;</c> name and ignores the projection alias, so staging under the member alias
/// would silently desynchronize emit and read. Duplicated Inner leaves (<c>new { a = r, b = r }</c>) share that
/// alias and are emitted once, or pipeline construction fails with a duplicate element name.
/// </para>
/// <para>
/// <b>A whole-entity leaf requires every sibling to be whole-document-readable.</b> Both fallback legs (explicit
/// DriverLinq, and a mid-compile <c>TryBuildNativeFactory</c> decline) strip the <c>Select</c> and shape whole
/// documents. A field leaf is read by its root-relative path; a computed leaf has no path, so mixing the two
/// declines (see <c>MongoShapedQueryCompilingExpressionVisitor.HasJoinScopeInnerEntityProjectionLeaf</c>).
/// </para>
/// <para>
/// Non-default serialization needs no guard here: <c>MongoExpressionTranslator.TryTranslateValue</c> already
/// applies <c>AllFieldsDefaultSerialized</c>.
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

        // Seeded with aliases already on the query: RebindInnerShaperToOuterQuery registered the inner entity's
        // projection under its "_lookup_<Nav>" alias. AddToProjection would uniquify a colliding user member, so
        // the shaper would read a renamed alias the $project never emitted — a silently dropped value. Decline.
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
            // A whole-entity leaf naming any scope in the chain: root (index 0) or a level's Inner side (1..N).
            // MongoTransparentScopeResolver.TryResolveScopeDepth resolves by parameter identity and member-name
            // chain, never by CLR type. Root stages $$ROOT under its own alias; Inner stages under the level's
            // fixed InnerPrefix (see the class remarks' "Alias space"; don't change it to the member alias).
            //
            // A leaf that is also an Include/ThenInclude target arrives wrapped in IncludeExpression; unwrap to
            // the outermost EntityExpression. A nested ThenInclude (on NavigationExpression) is left for the
            // shaper. A reference Include is admitted only when its target is a whole-entity Inner leaf of this
            // scope (nav-expansion's LeftJoin for `Include(o => o.Owner).Select(...)`), whose $lookup already is
            // the Include's lookup; anything else declines, symmetric with BindResultMember.
            var unwrappedLeafBody = leafBody;
            var includesResolvableInLeaf = true;
            var referenceIncludeLevels = new List<MongoJoinScopeLevel>();
            while (unwrappedLeafBody is IncludeExpression include)
            {
                if (include.Navigation is not { IsCollection: true })
                {
                    if (TryResolveReferenceIncludeLevel(mongoQ, scope, rootParam, include, out var referenceLevel))
                    {
                        referenceIncludeLevels.Add(referenceLevel);
                    }
                    else
                    {
                        includesResolvableInLeaf = false;
                    }
                }

                unwrappedLeafBody = include.EntityExpression;
            }

            if (includesResolvableInLeaf && MongoTransparentScopeResolver.TryResolveScopeDepth(
                    unwrappedLeafBody, rootParam, hopNames: ["Outer", "Inner"], sourceCount: scope.Levels.Count, out var scopeIndex))
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
                else if (!TryStageInnerLevel(scope.Levels[scopeIndex - 1], staged, seenAliases))
                {
                    return false;
                }

                // Each reference Include's target document must survive the $project for its NavigationExpression
                // to read; staged like a whole-entity Inner leaf, sharing its per-level dedup.
                foreach (var referenceLevel in referenceIncludeLevels)
                {
                    if (!TryStageInnerLevel(referenceLevel, staged, seenAliases))
                    {
                        return false;
                    }
                }

                continue;
            }

            // A nested wrapped leaf (`CustomerId = new { Id = o.Customer!.CustomerID }`), one level only, depth-1
            // scope only. Any unrecognized member declines the whole outer leaf. Separate from
            // NativeProjectionBinder.TryGetDocumentConstructionLeaf, though it builds the same node.
            if (scope.Levels.Count == 1
                && leafBody.TryGetProjectionMembers(out var nestedMembers))
            {
                var translatedNestedMembers = new List<(string, MongoExpression)>();
                var seenNestedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var declined = false;

                foreach (var (nestedMemberName, nestedValue) in nestedMembers)
                {
                    // Must be a MongoFieldExpression: ReadDocumentConstructionMemberTyped hard-casts to it, so a
                    // computed (MongoBinaryExpression) or outer-sourced (MongoOuterFieldExpression) member would
                    // be an InvalidCastException at compile time in default Native mode. Only inner-sourced plain
                    // fields are accepted.
                    //
                    // Unlike TryGetDocumentConstructionLeaf, dotted ElementNames are fine here (the Inner side
                    // lives under "_lookup_<Nav>" and both read legs split the path), and no
                    // HasDefaultKeySerialization check is needed: the join-scope translator already declines
                    // value-converted members, and reads here are property-aware
                    // (BsonBinding.CreateGetPropertyValueAtPath). Pinned by NativeJoinScopeNestedProjectionTests
                    // .Nested_projection_with_value_converted_member_falls_back.
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

            // Ordinary scalar/computed leaf. A chain must use TryTranslateSingleScope, which re-roots the leaf onto
            // the single scope its member-name chain names; TryTranslateValue's flat CLR-type check could resolve
            // against the wrong level's $lookup alias. A leaf spanning scopes declines the whole projection.
            //
            // A nav-null-check ternary member (`new { a = o != null ? o.OrderID : -1 }`) is tried first: the ordinary
            // arm has no ternary handling. A ternary whose test isn't a scope null check falls through unchanged.
            if (leafBody is ConditionalExpression conditionalLeaf
                && TryTranslateScopeNullCheckConditional(mongoQ, scope, rootParam, conditionalLeaf, out var conditionalValue))
            {
                if (!seenAliases.Add(alias))
                {
                    return false;
                }

                staged.Add(new MongoProjection(alias, conditionalValue));
                continue;
            }

            MongoExpression? computedLeaf;
            if (scope.Levels.Count > 1)
            {
                // A nested wrapped leaf after a chain is out of scope: TranslateOperand's generic
                // NewExpression -> MongoDocumentConstructionExpression path doesn't guarantee field members, which
                // the read side requires.
                if (leafBody.TryGetProjectionMembers(out _)
                    || !NativeJoinScopeTranslator.TryTranslateSingleScope(scope, rootParam, leafBody, valueMode: true, out computedLeaf))
                {
                    return false; // one untranslatable/cross-scope/nested leaf declines the whole projection — no partial commit
                }
            }
            else if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, leafBody, out computedLeaf))
            {
                return false; // one untranslatable leaf declines the whole projection — no partial commit
            }

            // A non-nullable Length/IndexOf over a possibly-null (or unmatched left-outer) string is rendered null-safely,
            // so the server answers null where EF relational throws "Nullable object must have a value.". Flag it
            // (MongoProjection.ThrowsOnNull): the read side reads it as T? and throws that, rather than reading 0. An
            // operator that may absorb the null ($max/$min, Sign) declines. This call is the flag's only source, as in
            // NativeProjectionBinder.TryTranslateLeaf.
            var nonNullableRead = MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead(
                NativeSlotPopulator.UnwrapBoxingToObjectType(leafBody), computedLeaf);
            if (nonNullableRead == NonNullableValueRead.Decline)
            {
                return false;
            }

            var throwsOnNull = nonNullableRead == NonNullableValueRead.ThrowOnNull;

            if (!seenAliases.Add(alias))
            {
                return false;
            }

            staged.Add(new MongoProjection(alias, computedLeaf, ThrowsOnNull: throwsOnNull));
        }

        // A whole-entity leaf forces both fallback legs to shape whole, un-projected documents, so every sibling
        // must be readable from one. Field leaves (MongoFieldExpression, and MongoOuterFieldExpression for
        // outer-side scalars) are read by root-relative path; a computed leaf is not ("Document element 'X' is
        // missing but required"). Decline the whole projection — the same rule as
        // NativeProjectionBinder.IsWholeDocumentReadableLeaf.
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
    /// Stages one level's whole Inner document under its fixed alias <see cref="MongoJoinScopeLevel.InnerPrefix"/>,
    /// deduplicated per level. Returns <see langword="false"/> only when the alias is already staged by something
    /// other than this level's Inner reference.
    /// </summary>
    private static bool TryStageInnerLevel(
        MongoJoinScopeLevel level, List<MongoProjection> staged, HashSet<string> seenAliases)
    {
        // Claim the alias explicitly (usually already seeded from mongoQ.Projection, so the result is ignored) so
        // the ordinary-leaf arm doesn't depend solely on that seeding to reject a user member named "_lookup_<Nav>".
        seenAliases.Add(level.InnerPrefix);

        // Per-level dedup: a duplicated Inner leaf, or an Inner leaf plus a reference Include of the same level,
        // stages once (a duplicate $project field would crash pipeline construction). The existing entry must
        // really be this level's Inner reference; otherwise decline rather than silently skip the document.
        var existingIndex = staged.FindIndex(p => p.Alias == level.InnerPrefix);
        if (existingIndex < 0)
        {
            staged.Add(new MongoProjection(
                level.InnerPrefix,
                new MongoElementRefExpression(level.InnerPrefix, level.InnerEntityType.ClrType)));
            return true;
        }

        return staged[existingIndex].Expression is MongoElementRefExpression existingRef
               && existingRef.Path == level.InnerPrefix;
    }

    /// <summary>
    /// Resolves a reference <see cref="IncludeExpression"/> wrapping a whole-entity join-scope leaf to the level whose
    /// <c>$lookup</c> backs it. Nav-expansion lowers a cross-collection reference Include to a join and sets
    /// <see cref="IncludeExpression.NavigationExpression"/> to that join's <c>ti.Inner</c>. Admitted only for a
    /// non-embedded reference whose NavigationExpression is exactly a whole-entity Inner leaf of a level joined on
    /// that same navigation (a nested ThenInclude there declines). <c>BindResultMember</c> relies on this.
    /// </summary>
    private static bool TryResolveReferenceIncludeLevel(
        MongoQueryExpression mongoQ, MongoJoinScope scope, ParameterExpression rootParam, IncludeExpression include,
        [NotNullWhen(true)] out MongoJoinScopeLevel? level)
    {
        level = null;
        if (include.Navigation is not INavigation { IsCollection: false } navigation
            || navigation.IsEmbedded()
            || !MongoTransparentScopeResolver.TryResolveScopeDepth(
                include.NavigationExpression, rootParam, hopNames: ["Outer", "Inner"], sourceCount: scope.Levels.Count,
                out var targetScopeIndex)
            || targetScopeIndex == 0
            || mongoQ.Joins[targetScopeIndex - 1].Navigation != navigation)
        {
            return false;
        }

        level = scope.Levels[targetScopeIndex - 1];
        return true;
    }

    /// <summary>
    /// Populates the native <c>$project</c> slot for a bare <c>Select</c> body that is a ternary null-checking a
    /// join scope's Inner side, <c>ti =&gt; ti.Inner != null ? ti.Inner.City : null</c>, at any chain depth.
    /// <see cref="TryBindProjection"/> never sees this shape (it handles only wrapped bodies).
    /// </summary>
    internal static bool TryBindConditionalProjection(
        MongoQueryExpression mongoQ, LambdaExpression selector, JoinInfo joinInfo)
    {
        if (mongoQ.Select.JoinScope is not { } scope
            || joinInfo.Lookup is null
            || mongoQ.Select.Projection.Count > 0
            || selector.Parameters.Count != 1
            || selector.Body is not ConditionalExpression conditional)
        {
            return false;
        }

        if (!TryTranslateScopeNullCheckConditional(mongoQ, scope, selector.Parameters[0], conditional, out var leaf))
        {
            return false;
        }

        mongoQ.Select.AddProjection(new MongoProjection(NativeProjectionBinder.SyntheticBareProjectionAlias, leaf));
        ConfirmEntireChain(mongoQ, scope);
        return true;
    }

    /// <summary>
    /// Translates a ternary null-checking ONE join scope level's Inner side and choosing between two branches —
    /// <c>ti.Inner != null ? ti.Inner.City : "&lt;none&gt;"</c> — into a <see cref="MongoConditionalExpression"/>
    /// over a <see cref="MongoLookupNullCheckExpression"/>. Shared by the BARE-body arm
    /// (<see cref="TryBindConditionalProjection"/>) and the WRAPPED-leaf arm of <see cref="TryBindProjection"/>
    /// (a ternary as one member of <c>new { ... }</c>, EF Core's own <c>Condition_on_entity_with_include</c> shape).
    /// Pure: mutates nothing, so either caller can decline afterwards without a partial commit.
    /// </summary>
    private static bool TryTranslateScopeNullCheckConditional(
        MongoQueryExpression mongoQ, MongoJoinScope scope, ParameterExpression rootParam, ConditionalExpression conditional,
        [NotNullWhen(true)] out MongoConditionalExpression? leaf)
    {
        leaf = null;

        if (!NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, rootParam, conditional.Test, out var scopeIndex, out var isNotNull))
        {
            return false;
        }

        // scopeIndex is 1-based over Levels (0 is the root, never returned); mongoQ.Joins is 0-based.
        var checkedJoin = mongoQ.Joins[scopeIndex - 1];
        var level = scope.Levels[scopeIndex - 1];

        // An inner Join drops unmatched rows, so the null check would be constant. Checked per level since a chain
        // can mix Join/LeftJoin. Not restricted to reference navigations: every JoinInfo.Lookup is ForceUnwind, so a
        // left-outer collection join yields one (Outer, Inner-or-missing) row per pair, and
        // MongoLookupNullCheckExpression's $ifNull treats the missing field as null.
        if (!level.IsLeftOuter || checkedJoin.Lookup is not { ForceUnwind: true })
        {
            return false;
        }

        if (!TryTranslateConditionalBranch(scope, rootParam, conditional.IfTrue, out var ifTrue)
            || !TryTranslateConditionalBranch(scope, rootParam, conditional.IfFalse, out var ifFalse))
        {
            return false;
        }

        leaf = new MongoConditionalExpression(new MongoLookupNullCheckExpression(level.InnerPrefix, isNotNull), ifTrue, ifFalse);
        return true;
    }

    /// <summary>
    /// Translates one branch of <see cref="TryBindConditionalProjection"/>'s ternary: a scope-rooted value via
    /// <see cref="NativeJoinScopeTranslator.TryTranslateSingleScope"/>, or a bare literal, which
    /// <c>TryTranslateSingleScope</c> can't handle because it resolves no scope. Also used by
    /// <see cref="NativeTranslation.NativeSlotPopulator"/>'s conditional sort-key arm.
    /// </summary>
    /// <remarks>
    /// Unlike <c>MongoExpressionTranslator.TranslateConditionalBranch</c>, a boxed typed-null branch
    /// (<c>(T?)null</c>) is not special-cased; it declines.
    /// </remarks>
    internal static bool TryTranslateConditionalBranch(
        MongoJoinScope scope, ParameterExpression rootParam, Expression branch,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        if (branch is ConstantExpression constant)
        {
            result = new MongoConstantExpression(constant.Value, forSerialization: null);
            return true;
        }

        return NativeJoinScopeTranslator.TryTranslateSingleScope(scope, rootParam, branch, valueMode: true, out result);
    }

    /// <summary>
    /// Registers every level's <c>$lookup</c> and confirms every level's candidate join, once each, regardless of
    /// which levels a leaf named. Each join recorded one candidate at <c>TranslateJoinCore</c> time, so each needs a
    /// confirmation or <see cref="MongoSelectDefinition.HasUnconfirmedCandidateJoin"/> trips; and at depth 1 nothing
    /// else registers the <c>$lookup</c>, so skipping it would silently drop the stage. For a chain the
    /// <c>AddLookup</c> calls are redundant (deduped by alias). Also called by
    /// <see cref="NativeCardinalityBinder.TryBindAggregate"/> for a Select-less scalar aggregate over a join scope.
    /// </summary>
    internal static void ConfirmEntireChain(MongoQueryExpression mongoQ, MongoJoinScope scope)
    {
        // Callers reach here only after IsSingleEligibleNativeJoinScope; fail loudly if a future caller skips it.
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
