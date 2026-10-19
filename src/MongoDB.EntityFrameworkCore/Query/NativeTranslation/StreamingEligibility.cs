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
    /// Eligible: a simple primary key, no TPH hierarchy, no skip navigations, and every navigation's target is
    /// itself eligible; collection navigations must be owned and their elements must have no navigations.
    /// </summary>
    public static bool IsEligible(IEntityType entityType)
        => IsEligible(entityType, new HashSet<IEntityType>());

    private static bool IsEligible(IEntityType entityType, HashSet<IEntityType> visiting)
    {
        if (!visiting.Add(entityType))
        {
            return true; // cycle guard
        }

        if (entityType.IsInHierarchy())
        {
            return false;
        }

        // Owned collection elements have a composite key (owner FK + synthesized ordinal) that the rewriter
        // resolves from the owner / loop counter, so only non-owned-type key properties count.
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
            if (!IsEligible(navigation.TargetEntityType, visiting))
            {
                return false;
            }

            // Non-owned navigations stream only as single references ($lookup + $unwind).
            if (!navigation.TargetEntityType.IsOwned() && navigation.IsCollection)
            {
                return false;
            }

            // The streaming rewriter can't handle includes inside a collection element (FindCollectionShaper
            // doesn't descend into one); it would throw NativeTranslationNotSupportedException at shaper
            // compile. Reject so the query uses the DOM shaper.
            if (navigation.IsCollection && navigation.TargetEntityType.GetNavigations().Any())
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
}
