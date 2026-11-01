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
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
#if EF8
using Microsoft.EntityFrameworkCore.Storage;
#else
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
#endif

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// The deferred tracking of the commonest tracked native one-pass row: a single entity with no shadow values, no navigation to fix up and
/// nothing else to record. Everything the replay needs is on the instance itself (its key values) and fixed per entity type, so one
/// instance of this class per concrete entity type serves every such row and the row allocates nothing for tracking.
/// Anything richer is a <see cref="MongoPendingTracking"/>.
/// </summary>
internal sealed class MongoSimpleTracking
{
    private readonly IKey _key;
    private readonly IEntityType _entityType;
    private readonly IClrPropertyGetter[] _keyGetters;

    public IEntityType EntityType => _entityType;

    public MongoSimpleTracking(IKey key, IEntityType entityType)
    {
        _key = key;
        _entityType = entityType;
        _keyGetters = [.. key.Properties.Select(p => p.GetGetter())];
    }

    /// <summary>
    /// Tracks <paramref name="instance"/>, or resolves it to the entity already tracked with its key, as EF would have when it
    /// materialized the row.
    /// </summary>
    /// <returns>The instance to yield.</returns>
    public object Replay(MongoQueryContext queryContext, object instance)
    {
        var keyValues = new object?[_keyGetters.Length];
        for (var i = 0; i < keyValues.Length; i++)
        {
            keyValues[i] = _keyGetters[i].GetClrValue(instance);
        }

        var entry = queryContext.ReplayTryGetEntry(_key, keyValues!, throwOnNullKey: true, out _)
                    ?? queryContext.ReplayStartTracking(_entityType, instance, MongoQueryContext.EmptyShadowValues);
        return entry.Entity;
    }
}
