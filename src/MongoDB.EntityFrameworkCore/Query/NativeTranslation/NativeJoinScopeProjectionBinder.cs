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
/// Populates the native <c>$project</c> slot for a wrapped <c>new {...}</c>/<c>MemberInit</c> <c>Select</c> composed directly after an eligible
/// <c>Join</c>/<c>LeftJoin</c> or chain of them (<see cref="MongoSelectDefinition.JoinScope"/>).
/// </summary>
/// <remarks>
/// Admitted leaves: a whole-entity leaf naming any scope in the chain, or a scalar/computed leaf. At depth 1 the latter uses
/// <see cref="NativeJoinScopeTranslator.TryTranslateValue"/>; for a chain it must resolve to exactly one scope via
/// <see cref="NativeJoinScopeTranslator.TryTranslateSingleScope"/>, since <c>TryTranslateValue</c>'s flat CLR-type check can match the wrong level
/// when a chained join reuses a target type. A leaf spanning scopes declines the whole projection. All leaves are staged, then committed in one
/// block (projections, every level's lookup, one confirmation per level via <see cref="ConfirmEntireChain"/>); nothing is mutated on a decline.
/// <para>
/// A whole-entity Outer leaf stages a <c>$$ROOT</c> reference (<see cref="MongoElementRefExpression.WholeRootDocumentPath"/>) under its own alias;
/// <c>BindResultMember</c> folds the join's shaper into the selector first so the leaf is rebound by index.
/// </para>
/// <para>
/// <b>Alias space.</b> Ordinary leaves are emitted under their member name (the shaper's <c>ProjectionMember</c>), deduped case-insensitively as
/// <c>MongoQueryExpression.AddToProjection</c> does. A whole-entity Inner leaf is the deliberate exception: it stages under the fixed
/// <see cref="MongoJoinScopeLevel.InnerPrefix"/> because <c>MongoProjectionBindingRemovingExpressionVisitor.VisitBinary</c>'s cross-collection arm
/// reads the navigation's <c>_lookup_&lt;Nav&gt;</c> name and ignores the projection alias. Duplicated Inner leaves share it and are emitted once,
/// or pipeline construction fails with a duplicate element name.
/// </para>
/// <para>
/// <b>A whole-entity leaf requires every sibling to be whole-document-readable.</b> Both fallback legs (explicit DriverLinq, mid-compile
/// <c>TryBuildNativeFactory</c> decline) strip the <c>Select</c> and shape whole documents; a field leaf is read by root-relative path, a computed
/// leaf has no path, so mixing declines (<c>MongoShapedQueryCompilingExpressionVisitor.HasJoinScopeInnerEntityProjectionLeaf</c>).
/// </para>
/// </remarks>
internal static class NativeJoinScopeProjectionBinder
{
    internal static bool TryBindProjection(
        MongoQueryExpression mongoQ, LambdaExpression selector, JoinInfo joinInfo)
    {
        if (!TryGetEligibleScope(mongoQ, selector, joinInfo, out var scope))
        {
            return false;
        }

        var rootParam = selector.Parameters[0];

        // A positional container (`new object[] { o, o.Customer }`) is admitted: each element is one `_ctorArg<N>` member read back by index
        // (BuildSelectManyResultShaper's container mode), with the container's boxing Convert peeled so a whole-entity element resolves to its scope.
        if (!selector.Body.TryGetProjectionMembers(out var members, allowContainerElements: true))
        {
            return false;
        }

        var isContainer = selector.Body is NewArrayExpression;

        var staged = new List<MongoProjection>();

        // Seeded with aliases already on the query (RebindInnerShaperToOuterQuery registered the inner entity under "_lookup_<Nav>"): AddToProjection would
        // uniquify a colliding user member and the shaper would read a renamed alias the $project never emitted, silently dropping a value. Decline.
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in mongoQ.Projection)
        {
            if (existing.Alias is { } existingAlias)
            {
                seenAliases.Add(existingAlias);
            }
        }

        foreach (var (alias, member) in members)
        {
            var leafBody = isContainer ? NativeProjectionBinder.UnwrapContainerElementBoxing(member) : member;

            // A whole-entity leaf naming any scope: root (index 0) or a level's Inner side (1..N), resolved by parameter identity and member-name chain, never CLR
            // type. Root stages $$ROOT under its own alias; Inner under the level's fixed InnerPrefix (see "Alias space").
            //
            // An Include/ThenInclude target arrives wrapped in IncludeExpression; unwrap to the outermost EntityExpression (a nested ThenInclude is left for the
            // shaper). A reference Include is admitted only when its target is a whole-entity Inner leaf of this scope (its $lookup already is the Include's);
            // anything else declines, symmetric with BindResultMember.
            var unwrappedLeafBody = leafBody;
            var includesResolvableInLeaf = true;
            var referenceIncludeLevels = new List<MongoJoinScopeLevel>();
            while (unwrappedLeafBody is IncludeExpression include)
            {
                // A collection or embedded (owned) Include needs nothing staged beyond the whole document.
                if (include.Navigation is not { IsCollection: true }
                    && !(include.Navigation is INavigation ownedNavigation && ownedNavigation.IsEmbedded()))
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
                    unwrappedLeafBody, rootParam, MongoTransparentScopeResolver.TransparentIdentifierHops,
                    sourceCount: scope.Levels.Count, out var scopeIndex))
            {
                if (scopeIndex == 0)
                {
                    if (!TryStage(
                            alias,
                            new MongoElementRefExpression(
                                MongoElementRefExpression.WholeRootDocumentPath, mongoQ.CollectionExpression.EntityType.ClrType)))
                    {
                        return false;
                    }
                }
                else if (!TryStageInnerLevel(scope.Levels[scopeIndex - 1], staged, seenAliases))
                {
                    return false;
                }

                // Each reference Include's target document must survive the $project for its NavigationExpression to read; staged like an Inner leaf, sharing its per-level dedup.
                foreach (var referenceLevel in referenceIncludeLevels)
                {
                    if (!TryStageInnerLevel(referenceLevel, staged, seenAliases))
                    {
                        return false;
                    }
                }

                continue;
            }

            // A construction whose members are ALL whole-entity Inner leaves (`o1 = new { o2 = ti.Inner }`): the wrapper has no projection of its own; each member's
            // level is staged whole (shared per level with a sibling leaf) and BindResultMember rebinds by index. Root (index 0) and Include-wrapped members decline.
            if (leafBody.TryGetProjectionMembers(out var wrapperMembers)
                && wrapperMembers.Count > 0
                && TryResolveEntityWrapperLevels(scope, rootParam, wrapperMembers, out var wrapperLevels))
            {
                foreach (var wrapperLevel in wrapperLevels)
                {
                    if (!TryStageInnerLevel(wrapperLevel, staged, seenAliases))
                    {
                        return false;
                    }
                }

                continue;
            }

            // A nested wrapped leaf (`CustomerId = new { Id = o.Customer!.CustomerID }`), one level, depth-1 scope only; any unrecognized member declines. Separate from
            // NativeProjectionBinder.TryGetDocumentConstructionLeaf, though it builds the same node.
            if (scope.Levels.Count == 1
                && leafBody.TryGetProjectionMembers(out var nestedMembers))
            {
                var translatedNestedMembers = new List<(string, MongoExpression)>();
                var seenNestedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var declined = false;

                foreach (var (nestedMemberName, nestedValue) in nestedMembers)
                {
                    // Must be a MongoFieldExpression: ReadDocumentConstructionMemberTyped hard-casts to it, so a computed or outer-sourced member would be an
                    // InvalidCastException at compile time in Native mode. Unlike TryGetDocumentConstructionLeaf, dotted ElementNames are fine (the Inner side lives under
                    // "_lookup_<Nav>" and both read legs split the path) and no HasDefaultKeySerialization check is needed: the join-scope translator declines
                    // value-converted members and reads are property-aware (NativeJoinScopeNestedProjectionTests.Nested_projection_with_value_converted_member_falls_back).
                    if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, nestedValue, out var nestedLeaf)
                        || nestedLeaf is not MongoFieldExpression
                        || !seenNestedMembers.Add(nestedMemberName))
                    {
                        declined = true;
                        break;
                    }

                    translatedNestedMembers.Add((nestedMemberName, nestedLeaf));
                }

                if (declined
                    || !TryStage(alias, new MongoDocumentConstructionExpression(leafBody, translatedNestedMembers)))
                {
                    return false;
                }

                continue;
            }

            // Ordinary scalar/computed leaf. A chain must use TryTranslateSingleScope (re-roots onto the single scope its member-name chain names; TryTranslateValue's
            // flat CLR-type check could resolve against the wrong level's alias). A leaf spanning scopes declines the whole projection.
            //
            // A nav-null-check ternary member is tried first: the ordinary arm has no ternary handling. Other ternaries fall through unchanged.
            if (leafBody is ConditionalExpression conditionalLeaf
                && TryTranslateScopeNullCheckConditional(mongoQ, scope, rootParam, conditionalLeaf, out var conditionalValue))
            {
                // Read back whole by alias: a branch construction its class map would misread must decline.
                if (NativeProjectionBinder.IsMisreadWholeValueLeaf(conditionalValue) || !TryStage(alias, conditionalValue))
                {
                    return false;
                }

                continue;
            }

            // A bare null-check member (`new { o.OrderID, NoCustomer = o.Customer == null }`): the ordinary arm has no entity-null comparison.
            if (TryTranslateScopeNullCheck(mongoQ, scope, rootParam, leafBody, out var nullCheckValue))
            {
                if (!TryStage(alias, nullCheckValue))
                {
                    return false;
                }

                continue;
            }

            MongoExpression? computedLeaf;
            if (scope.Levels.Count > 1)
            {
                // A nested wrapped leaf after a chain is out of scope: the generic NewExpression -> MongoDocumentConstructionExpression path doesn't guarantee field members, which the read side requires.
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

            // A non-nullable Length/IndexOf over a possibly-null (or unmatched left-outer) string is rendered null-safely, so the server answers null where EF throws
            // "Nullable object must have a value."; flag it (MongoProjection.ThrowsOnNull) so the read side throws rather than reading 0. An operator that may absorb
            // the null ($max/$min, Sign) declines. The flag's only source, as in NativeProjectionBinder.TryTranslateLeaf.
            var nonNullableRead = MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead(
                NativeSlotPopulator.UnwrapBoxingToObjectType(leafBody), computedLeaf);
            if (nonNullableRead == NonNullableValueRead.Decline)
            {
                return false;
            }

            var throwsOnNull = nonNullableRead == NonNullableValueRead.ThrowOnNull;

            // A ternary/coalesce over constructions is read back whole by alias through the type's class map, which may name elements differently (`Id` vs `_id`).
            if (NativeProjectionBinder.IsMisreadWholeValueLeaf(computedLeaf) || !TryStage(alias, computedLeaf, throwsOnNull))
            {
                return false;
            }
        }

        // A whole-entity leaf forces both fallback legs to shape whole documents, so every sibling must be readable from one: field leaves are read by
        // root-relative path, a computed leaf is not ("Document element 'X' is missing but required"). Same rule as NativeProjectionBinder.IsWholeDocumentReadableLeaf.
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

        bool TryStage(string alias, MongoExpression value, bool throwsOnNull = false)
        {
            if (!seenAliases.Add(alias))
            {
                return false;
            }

            staged.Add(new MongoProjection(alias, value, ThrowsOnNull: throwsOnNull));
            return true;
        }
    }

    /// <summary>The shared gate of the <c>Select</c>-over-join-scope binders: a join scope backed by a native <c>$lookup</c>, nothing projected yet, and a selector over the single transparent-identifier parameter.</summary>
    private static bool TryGetEligibleScope(
        MongoQueryExpression mongoQ, LambdaExpression selector, JoinInfo joinInfo,
        [NotNullWhen(true)] out MongoJoinScope? scope)
    {
        scope = mongoQ.Select.JoinScope;
        return scope is not null
               && joinInfo.Lookup is not null
               && mongoQ.Select.Projection.Count == 0
               && selector.Parameters.Count == 1;
    }

    /// <summary>Stages one level's whole Inner document under <see cref="MongoJoinScopeLevel.InnerPrefix"/>, deduplicated per level; <see langword="false"/> only when the alias is staged by something other than this level's Inner reference.</summary>
    private static bool TryStageInnerLevel(
        MongoJoinScopeLevel level, List<MongoProjection> staged, HashSet<string> seenAliases)
    {
        // Claim the alias explicitly (usually already seeded from mongoQ.Projection) so the ordinary-leaf arm doesn't depend solely on seeding to reject a user member named "_lookup_<Nav>".
        seenAliases.Add(level.InnerPrefix);

        // Per-level dedup (a duplicate $project field would crash pipeline construction): the existing entry must really be this level's Inner reference; otherwise decline.
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

    /// <summary>Whether every member of a construction is a bare whole-entity Inner leaf of some level (index &gt;= 1, no Include), yielding those levels in member order.</summary>
    private static bool TryResolveEntityWrapperLevels(
        MongoJoinScope scope, ParameterExpression rootParam, IReadOnlyList<(string MemberName, Expression Value)> members,
        [NotNullWhen(true)] out List<MongoJoinScopeLevel>? levels)
    {
        levels = [];
        foreach (var (_, value) in members)
        {
            if (!MongoTransparentScopeResolver.TryResolveScopeDepth(
                    value, rootParam, MongoTransparentScopeResolver.TransparentIdentifierHops,
                    sourceCount: scope.Levels.Count, out var index)
                || index == 0)
            {
                levels = null;
                return false;
            }

            levels.Add(scope.Levels[index - 1]);
        }

        return true;
    }

    /// <summary>
    /// Resolves a reference <see cref="IncludeExpression"/> wrapping a whole-entity join-scope leaf to the level whose <c>$lookup</c> backs it
    /// (nav-expansion sets <see cref="IncludeExpression.NavigationExpression"/> to the join's <c>ti.Inner</c>). Admitted only for a non-embedded
    /// reference whose NavigationExpression is exactly a whole-entity Inner leaf of a level joined on that navigation; a nested ThenInclude declines.
    /// </summary>
    private static bool TryResolveReferenceIncludeLevel(
        MongoQueryExpression mongoQ, MongoJoinScope scope, ParameterExpression rootParam, IncludeExpression include,
        [NotNullWhen(true)] out MongoJoinScopeLevel? level)
    {
        level = null;
        if (include.Navigation is not INavigation { IsCollection: false } navigation
            || navigation.IsEmbedded()
            || !MongoTransparentScopeResolver.TryResolveScopeDepth(
                include.NavigationExpression, rootParam, MongoTransparentScopeResolver.TransparentIdentifierHops,
                sourceCount: scope.Levels.Count, out var targetScopeIndex)
            || targetScopeIndex == 0
            || mongoQ.Joins[targetScopeIndex - 1].Navigation != navigation)
        {
            return false;
        }

        level = scope.Levels[targetScopeIndex - 1];
        return true;
    }

    /// <summary>Populates the <c>$project</c> slot for a bare <c>Select</c> body that is a ternary null-checking a join scope's Inner side (<c>ti.Inner != null ? ti.Inner.City : null</c>), at any chain depth; <see cref="TryBindProjection"/> only handles wrapped bodies.</summary>
    internal static bool TryBindConditionalProjection(
        MongoQueryExpression mongoQ, LambdaExpression selector, JoinInfo joinInfo)
    {
        if (!TryGetEligibleScope(mongoQ, selector, joinInfo, out var scope)
            || selector.Body is not ConditionalExpression conditional)
        {
            return false;
        }

        if (!TryTranslateScopeNullCheckConditional(mongoQ, scope, selector.Parameters[0], conditional, out var leaf)
            || NativeProjectionBinder.IsMisreadWholeValueLeaf(leaf))
        {
            return false;
        }

        mongoQ.Select.AddProjection(new MongoProjection(NativeProjectionBinder.SyntheticBareProjectionAlias, leaf));
        ConfirmEntireChain(mongoQ, scope);
        return true;
    }

    /// <summary>Populates the <c>$project</c> slot for a bare <c>Select</c> body that IS a join scope null check (<c>ti =&gt; ti.Inner == null</c>), at any chain depth, as a <c>bool</c> leaf under the synthetic <c>_v</c> alias; gated by <see cref="TryTranslateScopeNullCheck"/>.</summary>
    internal static bool TryBindNullCheckProjection(
        MongoQueryExpression mongoQ, LambdaExpression selector, JoinInfo joinInfo)
    {
        if (!TryGetEligibleScope(mongoQ, selector, joinInfo, out var scope)
            || !TryTranslateScopeNullCheck(mongoQ, scope, selector.Parameters[0], selector.Body, out var nullCheck))
        {
            return false;
        }

        mongoQ.Select.AddProjection(new MongoProjection(NativeProjectionBinder.SyntheticBareProjectionAlias, nullCheck));
        ConfirmEntireChain(mongoQ, scope);
        return true;
    }

    /// <summary>
    /// Translates a ternary null-checking ONE join scope level's Inner side into a <see cref="MongoConditionalExpression"/> over a
    /// <see cref="MongoLookupNullCheckExpression"/>. Shared by the bare-body arm and the wrapped-leaf arm of <see cref="TryBindProjection"/> (EF Core's
    /// <c>Condition_on_entity_with_include</c> shape). Pure, so either caller can decline afterwards.
    /// </summary>
    private static bool TryTranslateScopeNullCheckConditional(
        MongoQueryExpression mongoQ, MongoJoinScope scope, ParameterExpression rootParam, ConditionalExpression conditional,
        [NotNullWhen(true)] out MongoConditionalExpression? leaf)
    {
        leaf = null;

        if (!TryTranslateScopeNullCheck(mongoQ, scope, rootParam, conditional.Test, out var nullCheck)
            || !TryTranslateConditionalBranch(scope, rootParam, conditional.IfTrue, out var ifTrue)
            || !TryTranslateConditionalBranch(scope, rootParam, conditional.IfFalse, out var ifFalse))
        {
            return false;
        }

        leaf = new MongoConditionalExpression(nullCheck, ifTrue, ifFalse);
        return true;
    }

    /// <summary>
    /// Translates <c>rootParam.«hop chain» == null</c> / <c>!= null</c> naming ONE join scope level's Inner side into a
    /// <see cref="MongoLookupNullCheckExpression"/>, admitted only where the check is not degenerate. The single gate for every join-scope null check
    /// read as a value (ternary test, bare Select body, wrapped member, and <c>NativeSlotPopulator</c>'s <c>Where</c> conjunct). Pure.
    /// </summary>
    internal static bool TryTranslateScopeNullCheck(
        MongoQueryExpression mongoQ, MongoJoinScope scope, ParameterExpression rootParam, Expression test,
        [NotNullWhen(true)] out MongoLookupNullCheckExpression? nullCheck)
    {
        nullCheck = null;

        if (!NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, rootParam, test, out var scopeIndex, out var isNotNull))
        {
            return false;
        }

        // scopeIndex is 1-based over Levels (0 is the root, never returned); mongoQ.Joins is 0-based.
        var checkedJoin = mongoQ.Joins[scopeIndex - 1];
        var level = scope.Levels[scopeIndex - 1];

        // The left-outer requirement is a conservative decline, not a correctness guard: on an inner Join the $unwind already dropped unmatched rows, so the
        // check is constant and rendering it would still be right. Checked per level since a chain can mix Join/LeftJoin. The ForceUnwind half is the real
        // guard: MongoLookupNullCheckExpression's $ifNull tests the inner prefix as a single (possibly missing) sub-document, which holds only after the
        // lookup is unwound (an empty array is not null). Not restricted to reference navigations: every JoinInfo.Lookup is ForceUnwind.
        if (!level.IsLeftOuter || checkedJoin.Lookup is not { ForceUnwind: true })
        {
            return false;
        }

        nullCheck = new MongoLookupNullCheckExpression(level.InnerPrefix, isNotNull);
        return true;
    }

    /// <summary>
    /// Translates one branch of <see cref="TryBindConditionalProjection"/>'s ternary: a scope-rooted value via <see cref="NativeJoinScopeTranslator.TryTranslateSingleScope"/>,
    /// or a bare literal (which resolves no scope). Also used by <see cref="NativeTranslation.NativeSlotPopulator"/>'s conditional sort-key arm.
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
    /// Registers every level's <c>$lookup</c> and confirms every level's candidate join, once each, regardless of which levels a leaf named. Each join
    /// recorded a candidate at <c>TranslateJoinCore</c> time, so each needs a confirmation or <see cref="MongoSelectDefinition.HasUnconfirmedCandidateJoin"/>
    /// trips; at depth 1 nothing else registers the <c>$lookup</c>. Also called by <see cref="NativeCardinalityBinder.TryBindAggregate"/> for a Select-less
    /// scalar aggregate over a join scope.
    /// </summary>
    internal static void ConfirmEntireChain(MongoQueryExpression mongoQ, MongoJoinScope scope)
    {
        // Callers reach here only after TryAdmitSingleEligibleNativeJoinScope; fail loudly if a future caller skips it.
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
