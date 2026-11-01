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

using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions; // IsInHierarchy()

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>Decides whether an entity type can be materialized by the forward-only streaming reader.</summary>
internal static class StreamingEligibility
{
    /// <summary>
    /// Eligible: a simple primary key, no TPH hierarchy, no skip navigations, and every navigation the rewriter reads is itself eligible;
    /// owned collection navigations' elements must have only owned navigations. A non-owned navigation the rewriter does not read (a
    /// collection the query does not include, or anything past the first reference hop: the plan skips it, so its document field is
    /// ignored) does not count, so a bidirectional relationship does not disqualify a query that only includes one side.
    /// </summary>
    public static bool IsEligible(IEntityType entityType)
        => IsEligible(entityType, new HashSet<IEntityType>(), lookupCollections: [], ignoreNonOwned: false);

    /// <summary>
    /// As <see cref="IsEligible(IEntityType)"/> for a query that also includes the non-owned collection navigations in
    /// <paramref name="lookupCollections"/>, each read from a root-level <c>$lookup</c> array (see
    /// <see cref="IsEligibleLookupElement"/>).
    /// </summary>
    public static bool IsEligible(IEntityType entityType, IReadOnlyCollection<INavigation> lookupCollections)
        => IsEligible(entityType, new HashSet<IEntityType>(), lookupCollections, ignoreNonOwned: false);

    /// <summary>
    /// Whether the target of a lookup collection Include can be read as the elements of a root-level <c>$lookup</c> array: a plain entity
    /// (no hierarchy, no skip navigations, a simple key) with no eager-loaded navigations of its own. Its other navigations are not read
    /// (the element plan skips them), and a further Include off the element has its own, non-streamable lookup, which the query gate rejects.
    /// </summary>
    public static bool IsEligibleLookupElement(IEntityType elementType)
    {
        if (elementType.IsInHierarchy() || elementType.GetSkipNavigations().Any() || elementType.GetNavigations().Any(n => n.IsEagerLoaded))
        {
            return false;
        }

        var pk = elementType.FindPrimaryKey();
        return pk != null && pk.Properties.Count(p => !p.IsOwnedTypeKey()) == 1;
    }

    // ignoreNonOwned: the entity is read by a plan that skips its non-owned navigations (a lookup reference's target and its owned subtree).
    private static bool IsEligible(
        IEntityType entityType, HashSet<IEntityType> visiting, IReadOnlyCollection<INavigation> lookupCollections, bool ignoreNonOwned)
    {
        if (!visiting.Add(entityType))
        {
            return true; // cycle guard
        }

        if (entityType.IsInHierarchy())
        {
            return false;
        }

        // Owned collection elements' composite key is resolved by the rewriter from the owner / loop counter; only non-owned keys count.
        var pk = entityType.FindPrimaryKey();
        if (pk == null)
        {
            return false;
        }

        var nonOwnedKeyProps = pk.Properties.Count(p => !p.IsOwnedTypeKey());
        if (nonOwnedKeyProps > 1)
        {
            return false;
        }

        // A required owned reference is still eligible: the rewriter reproduces EF's "required but missing"
        // throw (see MongoStreamingEntityMaterializerRewriter.RewriteOwnedNavigation).
        foreach (var navigation in entityType.GetNavigations())
        {
            // An included lookup collection is read element by element from its $lookup array, not as part of this entity's own graph.
            if (lookupCollections.Contains(navigation))
            {
                if (!IsEligibleLookupElement(navigation.TargetEntityType))
                {
                    return false;
                }

                continue;
            }

            var nonOwned = !navigation.TargetEntityType.IsOwned();

            // The plan skips a non-owned navigation of a lookup target, and a non-owned collection the query does not include: the
            // document field, if any, is never read, so the navigation does not matter here.
            if (nonOwned && (ignoreNonOwned || navigation.IsCollection))
            {
                continue;
            }

            if (!IsEligible(navigation.TargetEntityType, visiting, lookupCollections, ignoreNonOwned || nonOwned))
            {
                return false;
            }

            // RewriteOwnedNavigation reads an owned reference's sub-document with only single owned references beneath it, so a collection
            // anywhere below an owned reference stays on the DOM shaper.
            if (!IsOwnerBackReference(navigation) && !navigation.IsCollection && navigation.TargetEntityType.IsOwned()
                && OwnsCollectionBelow(navigation.TargetEntityType))
            {
                return false;
            }

            // The rewriter streams owned subtrees inside a collection element (references and collections, to any depth); a
            // non-owned navigation there has no $lookup plan, so the query uses the DOM shaper.
            if (navigation.IsCollection && HasNonOwnedNavigation(navigation.TargetEntityType))
            {
                return false;
            }
        }

        if (entityType.GetSkipNavigations().Any())
        {
            return false;
        }

        return true;
    }

    // Both walks skip an owned type's navigation back to its owner: it makes the graph cyclic and is not part of the owned subtree.
    private static bool IsOwnerBackReference(INavigation navigation)
        => navigation.IsOnDependent && navigation.ForeignKey.IsOwnership;

    private static bool HasNonOwnedNavigation(IEntityType entityType)
        => entityType.GetNavigations().Any(
            n => !IsOwnerBackReference(n) && (!n.TargetEntityType.IsOwned() || HasNonOwnedNavigation(n.TargetEntityType)));

    private static bool OwnsCollectionBelow(IEntityType entityType)
        => entityType.GetNavigations().Any(n => !IsOwnerBackReference(n) && (n.IsCollection || OwnsCollectionBelow(n.TargetEntityType)));
}
