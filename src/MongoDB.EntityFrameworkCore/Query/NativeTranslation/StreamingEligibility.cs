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
        => IsEligible(entityType, new HashSet<IEntityType>(), lookupCollections, ignoreNonOwned: false, isRoot: true);

    /// <summary>
    /// Whether the target of a lookup collection Include can be read as the elements of a root-level <c>$lookup</c> array: a plain entity
    /// (no hierarchy, no skip navigations, a simple key) with no eager-loaded navigations of its own. Its other navigations are not read
    /// (the element plan skips them), and a further Include off the element has its own, non-streamable lookup, which the query gate rejects.
    /// </summary>
    public static bool IsEligibleLookupElement(IEntityType elementType)
    {
        // What the element owns is read with it, a skip navigation is never read unless included (which the rewriter refuses), and a document's
        // composite key is the `_id` sub-document.
        if (elementType.IsInHierarchy() && !IsReadableHierarchy(elementType))
        {
            return false;
        }

        return elementType.FindPrimaryKey() != null;
    }

    /// <summary>Whether an owned collection's element type is read by the one-pass reader: the whole-entity rules, applied to the element.</summary>
    public static bool IsEligibleOwnedElement(IEntityType elementType)
        => elementType.BaseType == null && !elementType.GetDirectlyDerivedTypes().Any() && IsEligible(elementType, [], [], ignoreNonOwned: false);

    // ignoreNonOwned: the entity is read by a plan that skips its non-owned navigations (a lookup reference's target and its owned subtree).
    private static bool IsEligible(
        IEntityType entityType, HashSet<IEntityType> visiting, IReadOnlyCollection<INavigation> lookupCollections, bool ignoreNonOwned,
        bool isRoot = false)
    {
        if (!visiting.Add(entityType))
        {
            return true; // cycle guard
        }

        // The root of a hierarchy is read by one plan holding the properties of every concrete type, which a derived type with navigations of its
        // own, or an entity reached through a navigation, cannot be.
        if (entityType.IsInHierarchy() && !(isRoot && IsReadableHierarchy(entityType)))
        {
            return false;
        }

        // Owned collection elements' composite key is resolved by the rewriter from the owner / loop counter; only non-owned keys count.
        var pk = entityType.FindPrimaryKey();
        if (pk == null)
        {
            return false;
        }

        // A composite key of a document root is the `_id` sub-document, which the reader descends into; an owned type with more than one key
        // of its own is not stored that way.
        var nonOwnedKeyProps = pk.Properties.Count(p => !p.IsOwnedTypeKey());
        if (nonOwnedKeyProps > 1 && !entityType.IsDocumentRoot())
        {
            return false;
        }

        // The root of a hierarchy also reads the owned navigations its derived types declare (a document holds those of one concrete type).
        var navigations = isRoot
            ? entityType.GetNavigations().Concat(entityType.GetDerivedTypes().SelectMany(t => t.GetDeclaredNavigations()))
            : entityType.GetNavigations();

        // A required owned reference is still eligible: the rewriter reproduces EF's "required but missing"
        // throw (see MongoStreamingEntityMaterializerRewriter.RewriteOwnedNavigation).
        foreach (var navigation in navigations)
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

            if (!IsEligible(navigation.TargetEntityType, visiting, lookupCollections, ignoreNonOwned || nonOwned, isRoot: nonOwned))
            {
                return false;
            }
        }

        return true;
    }

    // A discriminated hierarchy whose derived types add properties and owned navigations. Two derived types (or a derived type and the root) that
    // store an owned navigation under one element name would make the fill loop's dispatch ambiguous, so they are not read by one plan.
    private static bool IsReadableHierarchy(IEntityType entityType)
        => entityType.FindDiscriminatorProperty() != null
           && entityType.GetDerivedTypes().All(
               t => !t.GetDeclaredSkipNavigations().Any() && t.GetDeclaredNavigations().All(n => n.TargetEntityType.IsOwned()))
           && HasDistinctOwnedElementNames(entityType);

    private static bool HasDistinctOwnedElementNames(IEntityType entityType)
    {
        var seen = new HashSet<string>();
        return entityType.GetNavigations().Concat(entityType.GetDerivedTypes().SelectMany(t => t.GetDeclaredNavigations()))
            .Where(n => n.TargetEntityType.IsOwned())
            .All(n => n.TargetEntityType.GetContainingElementName() is not { } name || seen.Add(name));
    }
}
