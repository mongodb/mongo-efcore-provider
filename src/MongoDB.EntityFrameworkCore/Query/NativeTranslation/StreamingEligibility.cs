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
    /// Eligible: a simple single-property primary key; navigations are only single (reference) owned
    /// sub-documents whose target types are themselves eligible. No owned collections, no
    /// cross-collection / non-owned navigations, no TPH discriminator hierarchy. Scalar and mapped-array
    /// properties are always fine (read via their serializers).
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

            // The rewriter cannot yet stream a collection whose element type itself owns a collection
            // (collection-of-collection). Single owned references nested in a collection element, and
            // nested single references, remain allowed.
            if (navigation.IsCollection
                && navigation.TargetEntityType.GetNavigations().Any(n => n.IsCollection))
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
