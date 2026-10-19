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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

// Reference and collection Include chain recognition and confirmation over join scopes.
internal sealed partial class MongoQueryableMethodTranslatingExpressionVisitor
{
    /// <summary>
    /// Whether <paramref name="selector"/> is the <c>Select(x =&gt; IncludeExpression)</c> nav-expansion generates for a
    /// single-level root collection <c>Include</c>. Its <c>$lookup</c> is registered later by
    /// <see cref="MongoProjectionBindingExpressionVisitor"/>, so the Select must not be marked non-representable.
    /// </summary>
    private static bool IsSingleLevelCollectionIncludeSelector(LambdaExpression selector)
        => selector.Body is IncludeExpression { Navigation: INavigation navigation } includeExpression
           && includeExpression.EntityExpression == selector.Parameters[0]
           && navigation.IsCollection
           && !navigation.IsEmbedded();

    /// <summary>
    /// Recognizes <c>Select(ti =&gt; IncludeExpression(ti.Outer, nav, ...))</c> (or the <c>ti.Inner</c> mirror, or a longer
    /// <c>Outer</c>/<c>Inner</c> hop chain) : a single-level non-embedded collection <c>Include</c> over one side of a user
    /// <c>Join</c>/<c>GroupJoin</c>/<c>LeftJoin</c>. Structural only: the caller resolves which scope the hop chain names and
    /// must gate the join through <see cref="NativeJoinScopeGate.TryAdmitSingleEligibleNativeJoinScope"/> before confirming it.
    /// </summary>
    internal static IncludeExpression? TryGetCollectionIncludeOverJoinScope(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.IsTransparentIdentifierType()
            || selector.Body is not IncludeExpression { Navigation: INavigation navigation } includeExpression
            || !navigation.IsCollection
            || navigation.IsEmbedded())
        {
            return null;
        }

        var hop = includeExpression.EntityExpression;
        if (hop is not MemberExpression)
        {
            return null;
        }

        while (hop is MemberExpression member && member.IsTransparentIdentifierOuterOrInnerAccess())
        {
            hop = member.Expression;
        }

        return hop == selector.Parameters[0] ? includeExpression : null;
    }

    /// <summary>
    /// Recognizes a pure chain of one or more reference <c>Include</c>s: siblings nest via
    /// <see cref="IncludeExpression.EntityExpression"/>, <c>ThenInclude</c>s via <see cref="IncludeExpression.NavigationExpression"/>.
    /// Any collection level returns <see langword="null"/> (see <see cref="TryGetMixedReferenceAndCollectionIncludeChain"/>);
    /// both share <see cref="TryWalkIncludeChain"/> so they can't disagree on structure.
    /// </summary>
    /// <remarks>
    /// The base may be any number of <c>.Outer</c> hops. A user join followed by an Include looks the same by hop count and is
    /// declined by <see cref="TryConfirmReferenceIncludeChain"/>'s <c>Joins.Count != chain.Count</c> check: this method only
    /// recognizes structure; all semantic declines live in the confirm step.
    /// </remarks>
    internal static List<IncludeExpression>? TryGetReferenceIncludeChain(LambdaExpression selector)
        => TryGetReferenceIncludeChain(selector, out _);

    /// <summary>
    /// As <see cref="TryGetReferenceIncludeChain(LambdaExpression)"/>, also reporting which levels are transitive
    /// (<c>ThenInclude</c>-nested). Confirm checks the root declaring type only for non-transitive levels.
    /// </summary>
    internal static List<IncludeExpression>? TryGetReferenceIncludeChain(
        LambdaExpression selector, out HashSet<IncludeExpression> transitiveLevels)
    {
        if (!TryWalkIncludeChain(selector, out var referenceLevels, out transitiveLevels, out var collectionLevel)
            || collectionLevel != null
            || referenceLevels.Count == 0)
        {
            transitiveLevels = [];
            return null;
        }

        return referenceLevels;
    }

    /// <summary>
    /// Recognizes a reference-Include chain with exactly one non-embedded collection <c>Include</c> mixed in, as a sibling
    /// or a terminal <c>ThenInclude</c>. Pure reference chains and a bare collection Include belong to the other
    /// recognizers; the three don't overlap. The collection level is returned unconfirmed (its <c>$lookup</c> registers during
    /// projection binding); only the reference levels go through <see cref="TryConfirmReferenceIncludeChain"/>.
    /// </summary>
    internal static bool TryGetMixedReferenceAndCollectionIncludeChain(
        LambdaExpression selector,
        out List<IncludeExpression> referenceLevels,
        out HashSet<IncludeExpression> transitiveLevels,
        out IncludeExpression? collectionLevel)
    {
        if (!TryWalkIncludeChain(selector, out referenceLevels, out transitiveLevels, out collectionLevel)
            || collectionLevel == null
            || referenceLevels.Count == 0)
        {
            referenceLevels = [];
            transitiveLevels = [];
            collectionLevel = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Shared structural walk for the reference/mixed Include-chain recognizers: follows the sibling spine
    /// (<see cref="IncludeExpression.EntityExpression"/>) and each sibling's linear <c>ThenInclude</c> chain, sorting
    /// navigations into <paramref name="referenceLevels"/> or a single <paramref name="collectionLevel"/>;
    /// <c>ThenInclude</c> levels are also recorded in <paramref name="transitiveLevels"/>.
    /// </summary>
    /// <remarks>
    /// An embedded navigation in a <c>ThenInclude</c> chain is transparent, provided nothing non-embedded is nested past it
    /// (<see cref="HasNonEmbeddedThenInclude"/>); an embedded sibling rejects the chain (see
    /// <see cref="IsOwnedEmbeddedIncludeSelector"/>). The base must be a pure <c>.Outer</c>* chain reaching the
    /// <c>TransparentIdentifier</c> parameter.
    /// </remarks>
    private static bool TryWalkIncludeChain(
        LambdaExpression selector,
        out List<IncludeExpression> referenceLevels,
        out HashSet<IncludeExpression> transitiveLevels,
        out IncludeExpression? collectionLevel)
    {
        referenceLevels = [];
        transitiveLevels = [];
        collectionLevel = null;

        if (selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.IsTransparentIdentifierType())
        {
            return false;
        }

        var parameter = selector.Parameters[0];
        var body = selector.Body;

        while (body is IncludeExpression { Navigation: INavigation navigation } include)
        {
            if (navigation.IsEmbedded())
            {
                return false;
            }

            if (navigation.IsCollection)
            {
                if (collectionLevel != null)
                {
                    // A second collection Include in the chain — out of scope, decline the whole shape.
                    return false;
                }

                collectionLevel = include;
            }
            else
            {
                referenceLevels.Add(include);
            }

            // Follow this level's linear ThenInclude chain (NavigationExpression axis).
            var thenIncludeLevel = include;
            var thenIncludeTargetType = navigation.TargetEntityType;
            while (thenIncludeLevel.NavigationExpression is IncludeExpression { Navigation: INavigation thenNav } thenInclude)
            {
                if (thenNav.IsEmbedded())
                {
                    // Owned auto-include: lives in the same document (EF-368). With nothing non-embedded nested past
                    // it, this level's ThenInclude chain ends here.
                    if (!HasNonEmbeddedThenInclude(thenInclude))
                    {
                        break;
                    }

                    // A reference Include declared on the owned type (Include(Buyer).ThenInclude(Address).ThenInclude(Region)): the
                    // confirm step verifies the Region $lookup's localField reads through the owned hop. Exactly one such reference.
                    IncludeExpression? behindOwned = null;
                    if (!TryFindReferenceBehindOwned(thenInclude, thenNav.DeclaringEntityType, ref behindOwned)
                        || behindOwned is null)
                    {
                        return false;
                    }

                    referenceLevels.Add(behindOwned);
                    transitiveLevels.Add(behindOwned);
                    thenIncludeLevel = behindOwned;
                    thenIncludeTargetType = ((INavigation)behindOwned.Navigation!).TargetEntityType;
                    continue;
                }

                if (thenInclude.EntityExpression is IncludeExpression
                    || thenNav.DeclaringEntityType != thenIncludeTargetType)
                {
                    // A sibling off a ThenInclude, or a hop not declared on the previous hop's target.
                    return false;
                }

                if (thenNav.IsCollection)
                {
                    // A collection ThenInclude off a reference level (Include(o => o.Customer.Orders)) is
                    // admitted as the chain's single collection level, but only as the terminal hop.
                    if (collectionLevel != null || thenInclude.NavigationExpression is IncludeExpression)
                    {
                        return false;
                    }

                    collectionLevel = thenInclude;
                    break;
                }

                referenceLevels.Add(thenInclude);
                transitiveLevels.Add(thenInclude);
                thenIncludeLevel = thenInclude;
                thenIncludeTargetType = thenNav.TargetEntityType;
            }

            body = include.EntityExpression;
        }

        var current = body;
        while (current is MemberExpression { Member.Name: "Outer" } outerAccess)
        {
            current = outerAccess.Expression;
        }

        return current == parameter;
    }

    /// <summary>
    /// Validates and confirms every navigation in a recognized reference-Include chain, all-or-nothing. A single Include
    /// builds and registers the forced-unwind <c>$lookup</c>; for siblings <c>TranslateJoinCore</c> already registered every
    /// join's, so this only confirms each. Registration makes <see cref="MongoQueryExpression.UsesDriverJoinFields"/> false so
    /// the lowerer, DOM shaper and driver-LINQ <c>StripJoinForLookup</c> agree on the <c>_lookup_&lt;Nav&gt;</c> field.
    /// </summary>
    /// <remarks>
    /// Registration also changes the fallback's pipeline at translation time, before <c>MongoQueryMode</c> is read, so a
    /// wrong admission is wrong in every mode; every conjunct below must hold on its own merits. <c>TryWalkIncludeChain</c>
    /// alone decides which <c>ThenInclude</c> nesting is admissible; don't re-check <c>HasNonEmbeddedThenInclude</c> per level
    /// (it would wrongly decline an admitted further level).
    /// </remarks>
    private static bool TryConfirmReferenceIncludeChain(
        MongoQueryExpression mongoQueryExpression,
        List<IncludeExpression> chain,
        HashSet<IncludeExpression> transitiveLevels)
    {
        // Joins are counted, not InnerCollections (keyed by entity type, which collapses same-target siblings). A join beyond
        // the chain's levels is admitted only as a FILTER join (TryAdmitFilterJoins).
        var extraJoins = mongoQueryExpression.Joins.Count - chain.Count;
        if (mongoQueryExpression.Select.HasTerminalOperator                // composed after a terminal
            || mongoQueryExpression.Select.SawNonBareJoinInner             // a filtered/non-bare-scan inner, any join
            || extraJoins < 0
            || (extraJoins > 0 && !TryAdmitFilterJoins(mongoQueryExpression, chain, extraJoins)))
        {
            return false;
        }

        var pendingByAlias = mongoQueryExpression.GetPendingLookups().ToDictionary(l => l.As);
        var newLookups = new List<LookupExpression>();

        foreach (var include in chain)
        {
            var navigation = (INavigation)include.Navigation!;

            // Query filters are caught by SawNonBareJoinInner above, not by checking the target's GetQueryFilter(): that misses
            // a filter declared on a TPH root and EF10 named filters, and the flat $lookup can't filter, so either would return
            // wrong rows in every mode. DeclaringEntityType is checked against the root only for non-transitive levels.
            if (navigation.ForeignKey.Properties.Count != 1                        // composite FK
                || navigation.ForeignKey.PrincipalKey.Properties.Count != 1        // composite PK
                || (!transitiveLevels.Contains(include)
                    && navigation.DeclaringEntityType != mongoQueryExpression.CollectionExpression.EntityType)
                || !mongoQueryExpression.InnerCollections.ContainsKey(navigation.TargetEntityType))
            {
                return false;
            }

            var alias = LookupExpression.GetLookupAlias(navigation);
            if (pendingByAlias.TryGetValue(alias, out var existing))
            {
                // Registered by TranslateJoinCore's multi-join flattening; confirm rather than re-register. A level whose own lookup
                // carries a join-inner filter is an explicit join, not an Include: declined (a query-filtered Include target is a
                // separate feature with its own required/optional-FK rules).
                if (!existing.ForceUnwind || existing.HasInnerPredicate)
                {
                    return false;
                }

                // A reference declared on an owned type (Include(Buyer).ThenInclude(Address).ThenInclude(Region)): the
                // lookup must read its key through exactly that owned path under the parent's alias.
                if (navigation.DeclaringEntityType.IsOwned()
                    && !OwnedHopLocalFieldMatches(chain, navigation, existing))
                {
                    return false;
                }
            }
            else if (navigation.DeclaringEntityType.IsOwned())
            {
                return false; // an owned hop always follows a prior join, so its lookup is already registered
            }
            else
            {
                // Single join: TranslateJoinCore never flattens a lone join, so register it here, using the recorded join's own
                // lookup: it carries the real left-outer flag from the operator EF chose. EF emits an inner Join only for a
                // dependent-side required navigation; a principal-side reference Include is always a LeftJoin even when the FK is
                // required, so guessing from ForeignKey.IsRequired dropped principals with no dependent.
                var join = mongoQueryExpression.Joins.FirstOrDefault(
                    j => j.Navigation == navigation && j.Lookup is { ForceUnwind: true });
                if (join is null || join.Lookup!.HasInnerPredicate)
                {
                    return false;
                }

                // Defensive: not known reachable, since the lookup's constructor never prefixes LocalField and the
                // sites that do mutate already-registered lookups.
                if (join.Lookup!.LocalField.StartsWith(LookupExpression.LookupAliasPrefix, StringComparison.Ordinal))
                {
                    return false;
                }

                newLookups.Add(join.Lookup);
            }
        }

        foreach (var lookup in newLookups)
        {
            mongoQueryExpression.AddLookup(lookup);
        }

        // Candidates equal Joins.Count, so confirm every join; a filter join no earlier arm confirmed is confirmed here.
        var toConfirm = mongoQueryExpression.Joins.Count - mongoQueryExpression.Select.ConfirmedReferenceIncludeCount;
        for (var i = 0; i < toConfirm; i++)
        {
            mongoQueryExpression.Select.MarkReferenceIncludeConfirmed();
        }

        return true;
    }

    /// <summary>
    /// Whether the joins beyond a reference-Include chain's own levels are FILTER joins that may precede the Include: a Where
    /// that read a join's Inner left its predicate, and any paging/Distinct after it, in <c>PostJoinOps</c> (lowered after
    /// every <c>$lookup</c>). The Include's join is a left-outer reference (row-preserving), so that equals paging before it.
    /// Every filter join must be a resolved, flattened, eligible reference join, confirmed by an earlier arm or read by a
    /// native post-join predicate.
    /// </summary>
    private static bool TryAdmitFilterJoins(MongoQueryExpression query, List<IncludeExpression> chain, int extraJoins)
    {
        var select = query.Select;
        var alreadyConfirmed = select.ConfirmedReferenceIncludeCount;
        if (alreadyConfirmed != extraJoins && !(alreadyConfirmed == 0 && select.JoinInnerAccessConfirmed))
        {
            return false;
        }

        // Every join no Include level backs (matched by navigation, once each) must be a reference join.
        var remaining = chain.Select(include => (INavigation)include.Navigation!).ToList();
        foreach (var join in query.Joins)
        {
            if (join.Navigation is { } navigation && remaining.Remove(navigation))
            {
                continue;
            }

            if (join.Lookup is not { ForceUnwind: true }
                || join.Navigation is not { IsCollection: false }
                || !join.IsNativelyEligible)
            {
                return false;
            }
        }

        // Paging recorded after a join but still in PipelineOps lowers BEFORE every $lookup.
        return !(select.HasPaging && select.HasPagingRecordedAfterAJoin && !query.AreAllJoinsRowCountPreserving());
    }

    /// <summary>
    /// Whether <paramref name="lookup"/> (the registered <c>$lookup</c> of <paramref name="navigation"/>, declared on an owned
    /// type) reads its foreign key at <c>&lt;parent alias&gt;.&lt;owned path&gt;.&lt;fk element&gt;</c>: the parent is the one chain
    /// level targeting the owned path's root owner, and every owned hop between is a single reference.
    /// </summary>
    private static bool OwnedHopLocalFieldMatches(
        List<IncludeExpression> chain, INavigation navigation, LookupExpression lookup)
    {
        var segments = new List<string>();
        var type = navigation.DeclaringEntityType;
        while (type.IsOwned())
        {
            var ownership = type.FindOwnership();
            if (ownership is null
                || ownership.PrincipalToDependent is not { IsCollection: false }
                || type.GetContainingElementName() is not { } segment)
            {
                return false;
            }

            segments.Insert(0, segment);
            type = ownership.PrincipalEntityType;
        }

        var parents = chain
            .Select(include => (INavigation)include.Navigation!)
            .Where(parent => parent.TargetEntityType == type)
            .ToList();
        if (parents.Count != 1)
        {
            return false; // ambiguous: sibling includes of the same target type
        }

        var expected = $"{LookupExpression.GetLookupAlias(parents[0])}.{string.Join('.', segments)}."
                       + LookupExpression.GetFieldPath(navigation.ForeignKey.Properties[0]);
        return lookup.LocalField == expected;
    }

    /// <summary>
    /// Finds the one non-embedded reference <see cref="IncludeExpression"/> behind a chain of embedded single-reference
    /// (owned) hops, searching both axes (EF nests the owned sibling holding the reference on either). <see langword="false"/>
    /// declines the whole chain: a second non-embedded include, a collection behind the hop, or a non-embedded include not
    /// declared on an owned type. <paramref name="owner"/> declares the embedded navigations being examined.
    /// </summary>
    private static bool TryFindReferenceBehindOwned(Expression expression, IEntityType owner, ref IncludeExpression? found)
    {
        if (expression is not IncludeExpression include)
        {
            return true;
        }

        if (include.Navigation is not INavigation navigation)
        {
            return false;
        }

        if (navigation.IsEmbedded())
        {
            // A $lookup key read through an array has no element-level correlation; only a plain owned auto-include
            // (nothing non-embedded under it) may be a collection.
            if (navigation.IsCollection)
            {
                return !HasNonEmbeddedThenInclude(include);
            }

            // The same owner's sibling auto-includes, then the owned type's own includes one hop down.
            return TryFindReferenceBehindOwned(include.EntityExpression, owner, ref found)
                   && TryFindReferenceBehindOwned(include.NavigationExpression, navigation.TargetEntityType, ref found);
        }

        // A non-embedded include is admitted only as a reference declared on an owned type, and only one of them; the
        // owner's sibling auto-includes that follow it on the entity axis are walked with the same rule.
        if (navigation.IsCollection || found is not null || !owner.IsOwned() || navigation.DeclaringEntityType != owner)
        {
            return false;
        }

        found = include;
        return TryFindReferenceBehindOwned(include.EntityExpression, owner, ref found);
    }

    /// <summary>
    /// Whether <paramref name="navigationExpression"/> is, or contains through further embedded hops, an
    /// <see cref="IncludeExpression"/> with a non-embedded navigation: a real <c>ThenInclude</c> past the looked-up document,
    /// not an owned auto-include living in the same document. Recurses both axes; known shapes are already rejected by the
    /// <c>Joins.Count != chain.Count</c> check, so the recursion is defence in depth.
    /// </summary>
    private static bool HasNonEmbeddedThenInclude(Expression navigationExpression)
    {
        if (navigationExpression is not IncludeExpression nested)
        {
            return false;
        }

        if (nested.Navigation is not INavigation nestedNavigation || !nestedNavigation.IsEmbedded())
        {
            return true;
        }

        return HasNonEmbeddedThenInclude(nested.EntityExpression) || HasNonEmbeddedThenInclude(nested.NavigationExpression);
    }

    /// <summary>
    /// Whether <paramref name="selector"/> is the <c>Select(x =&gt; IncludeExpression(...))</c> nav-expansion generates for
    /// auto-included owned (embedded) navigations, possibly nested. Owned data lives in the owner's document, so there is
    /// nothing to push down and the Select must not be marked non-representable; any non-embedded navigation falls back.
    /// Owned collections whose elements have further navigations are kept off the streaming shaper by
    /// <see cref="StreamingEligibility"/>.
    /// </summary>
    private static bool IsOwnedEmbeddedIncludeSelector(LambdaExpression selector)
    {
        if (selector.Parameters.Count != 1)
        {
            return false;
        }

        var body = selector.Body;
        var sawInclude = false;

        while (body is IncludeExpression { Navigation: INavigation navigation } include)
        {
            // Embedded arrays and sub-documents need no extra pipeline stage.
            if (!navigation.IsEmbedded())
            {
                return false;
            }

            sawInclude = true;
            body = include.EntityExpression;
        }

        return sawInclude && body == selector.Parameters[0];
    }
}
