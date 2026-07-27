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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>Decides whether an entity type can be materialized by the forward-only streaming reader.</summary>
internal static class StreamingEligibility
{
    /// <summary>
    /// Eligible: a simple single-property primary key; navigations are single (reference) owned
    /// sub-documents OR owned collections, each whose target type is itself eligible AND (for a collection)
    /// whose element carries no navigations of its own — no cross-collection / non-owned navigations, no
    /// TPH discriminator hierarchy. Scalar and mapped-array properties are always fine (read via their
    /// serializers).
    /// </summary>
    public static bool IsEligible(IEntityType entityType)
        => IsEligible(entityType, new HashSet<IEntityType>());

    private static bool IsEligible(IEntityType entityType, HashSet<IEntityType> visiting)
    {
        if (!visiting.Add(entityType))
        {
            return true; // already validating this type (avoid cycles)
        }

        // No discriminator hierarchy (single concrete type only).
        if (entityType.BaseType != null || entityType.GetDirectlyDerivedTypes().Any())
        {
            return false;
        }

        // Primary key. A document-root entity needs a simple single-property primary key. An owned
        // collection element type legitimately carries a composite key (the owner FK + a synthesized
        // ordinal); those extra properties are owned-type keys, resolved against the owner / loop counter
        // by the rewriter, so allow a composite key whose non-leaf properties are all owned-type keys.
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

        // Only single (reference) owned navigations, to eligible owned types. (A required owned reference is
        // still eligible — the rewriter reproduces EF's "required but missing" throw via the present flag;
        // see MongoStreamingEntityMaterializerRewriter.RewriteOwnedNavigation.)
        foreach (var navigation in entityType.GetNavigations())
        {
            // The navigation's target type must itself be streaming-eligible (recursively; the
            // `visiting` cycle-guard prevents infinite recursion on bidirectional relationships).
            if (!IsEligible(navigation.TargetEntityType, visiting))
            {
                return false;
            }

            // Non-owned navigations are only supported as single references (materialized via
            // $lookup + $unwind). A non-owned collection navigation is not yet streamable.
            if (!navigation.TargetEntityType.IsOwned() && navigation.IsCollection)
            {
                return false;
            }

            // The streaming rewriter's forward-only reader has no IncludeExpression case for a collection
            // element (FindCollectionShaper doesn't descend into one), so a collection whose element
            // carries ANY navigation of its own — a nested owned single reference just as much as a
            // nested owned/non-owned collection — is streaming-ineligible: it crashes at shaper-compile
            // with NativeTranslationNotSupportedException rather than materializing correctly. Reject any
            // such collection here so it routes to the native DOM shaper instead, which handles it fine.
            // Collection-of-collection was already rejected by this check before; a collection element with
            // a nested single reference is a NEW rejection (EF-322 owned-collection slice) — it used to be
            // (wrongly) deemed streaming-eligible.
            if (navigation.IsCollection && navigation.TargetEntityType.GetNavigations().Any())
            {
                return false;
            }
        }

        // Skip-navigations make it ineligible.
        if (entityType.GetSkipNavigations().Any())
        {
            return false;
        }

        return true;
    }
}
