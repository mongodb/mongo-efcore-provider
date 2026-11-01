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
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// The tracking a native one-pass row deferred: the <c>TryGetEntry</c>/<c>StartTracking</c> calls EF's materializer made while the
/// driver deserialized the row (see <see cref="MongoQueryContext.BeginDeferral"/>), in the order EF made them. They are replayed, in
/// that order, only when the enumerator reaches the row, so rows of the driver's batch the caller never reads are never tracked.
/// </summary>
internal sealed class MongoPendingTracking
{
    private readonly List<Entity> _entities = new(1);
    private List<(object Entity, INavigation Navigation)>? _loaded;
    private Entity? _open;

    /// <summary>EF looked up an entity by key: opens a record that the following <c>StartTracking</c> completes.</summary>
    public void Begin(IKey key, object[] keyValues, bool throwOnNullKey)
    {
        _open = new Entity(key, keyValues, throwOnNullKey);
        _entities.Add(_open);
    }

#if EF8
    /// <summary>EF materialized the entity it just looked up and started tracking it.</summary>
    public void Complete(IEntityType entityType, object instance, ValueBuffer snapshot)
#else
    /// <summary>EF materialized the entity it just looked up and started tracking it.</summary>
    public void Complete(IEntityType entityType, object instance, ISnapshot snapshot)
#endif
    {
        var entity = _open ?? throw new InvalidOperationException(
            "Internal error - the materializer started tracking an entity it had not looked up.");
        entity.EntityType = entityType;
        entity.Instance = instance;
        entity.Snapshot = snapshot;
        _open = null;
    }

    /// <summary>An include whose navigation EF would have marked loaded on the (then tracked) entry.</summary>
    public void RecordLoaded(object entity, INavigation navigation)
        => (_loaded ??= []).Add((entity, navigation));

    /// <summary>
    /// Replays the deferred tracking against <paramref name="queryContext"/>'s state manager, as EF would have done row by row.
    /// </summary>
    /// <param name="queryContext">The query's context.</param>
    /// <param name="result">The instance the row's materializer returned.</param>
    /// <returns>The instance to yield: the already tracked one when the state manager resolved the row's key to an existing entity.</returns>
    public object Replay(MongoQueryContext queryContext, object result)
    {
        var canonical = result;
        foreach (var entity in _entities)
        {
            var entry = queryContext.ReplayTryGetEntry(entity.Key, entity.KeyValues, entity.ThrowOnNullKey, out _);
            if (entry == null && entity.EntityType != null)
            {
                entry = queryContext.ReplayStartTracking(entity.EntityType, entity.Instance!, entity.Snapshot);
            }

            entity.Entry = entry;
            if (entry != null && ReferenceEquals(entity.Instance, result))
            {
                canonical = entry.Entity;
            }
        }

        if (_loaded != null)
        {
            foreach (var (instance, navigation) in _loaded)
            {
                _entities.Find(e => ReferenceEquals(e.Instance, instance))?.Entry?.SetIsLoaded(navigation);
            }
        }

        return canonical;
    }

    private sealed class Entity(IKey key, object[] keyValues, bool throwOnNullKey)
    {
        public IKey Key { get; } = key;
        public object[] KeyValues { get; } = keyValues;
        public bool ThrowOnNullKey { get; } = throwOnNullKey;
        public IEntityType? EntityType { get; set; }
        public object? Instance { get; set; }
#if EF8
        public ValueBuffer Snapshot { get; set; }
#else
        public ISnapshot Snapshot { get; set; } = null!;
#endif
        public InternalEntityEntry? Entry { get; set; }
    }
}
