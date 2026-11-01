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
using System.Linq;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Provides the contextual information for a given query such as which client should execute it and using which dependencies.
/// </summary>
public class MongoQueryContext : QueryContext
{
    /// <summary>
    /// Create a <see cref="MongoQueryContext"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryContextDependencies"/> this context specifies.</param>
    /// <param name="mongoClient">The <see cref="IMongoClientWrapper"/> to use in executing this query.</param>
    public MongoQueryContext(
        QueryContextDependencies dependencies,
        IMongoClientWrapper mongoClient)
        : base(dependencies)
    {
        MongoClient = mongoClient;
    }

    /// <summary>
    /// The <see cref="IMongoClientWrapper"/> this query should be executed using.
    /// </summary>
    public virtual IMongoClientWrapper MongoClient { get; }

    // While set, the native one-pass materializer is constructing a row inside the driver's batch deserialization: tracking is recorded,
    // not performed (see MongoPendingTracking).
    private bool _deferring;

    // The full record, once a row needs one: more than one tracked entity, shadow values, or an include to mark loaded.
    private MongoPendingTracking? _pending;

    // Until then the row's single entity is held here, so the common flat row allocates nothing to track.
    private IKey? _inlineKey;
    private object[]? _inlineKeyValues;
    private bool _inlineThrowOnNullKey;
    private IEntityType? _inlineEntityType;
    private object? _inlineInstance;

    private MongoSimpleTracking? _lastSimple;
    private Dictionary<IEntityType, MongoSimpleTracking>? _simpleByType;

    /// <summary>
    /// Starts deferring the tracking of the row being materialized: until <see cref="EndDeferral"/>, EF's identity lookup and
    /// <c>StartTracking</c> calls are recorded rather than performed.
    /// </summary>
    internal void BeginDeferral()
    {
        _deferring = true;
        _pending = null;
        ClearInline();
    }

    /// <summary>
    /// Stops deferring and returns what must happen when the enumerator reaches the row: <see langword="null"/> if nothing was recorded,
    /// a shared <see cref="MongoSimpleTracking"/> for a plain single-entity row, otherwise the row's <see cref="MongoPendingTracking"/>.
    /// </summary>
    /// <param name="result">The instance the row's materializer returned.</param>
    internal object? EndDeferral(object? result)
    {
        object? deferred;
        if (_pending != null)
        {
            deferred = _pending;
        }
        else if (_inlineKey == null)
        {
            deferred = null;
        }
        else if (_inlineEntityType != null && ReferenceEquals(_inlineInstance, result))
        {
            deferred = GetSimple(_inlineKey, _inlineEntityType);
        }
        else
        {
            // A lookup that never started tracking, or an entity that is not the row's result: keep the full record.
            deferred = Promote();
        }

        _deferring = false;
        _pending = null;
        ClearInline();
        return deferred;
    }

    /// <summary>
    /// As <see cref="EndDeferral"/> for a projected row, which has no single result instance: a row whose only recorded tracking is one plain
    /// entity gets a <see cref="MongoSimpleRowTracking"/> (one small object) rather than a full <see cref="MongoPendingTracking"/>.
    /// </summary>
    internal object? EndRowDeferral()
    {
        if (_pending != null || _inlineKey == null || _inlineEntityType == null)
        {
            return EndDeferral(null);
        }

        var deferred = new MongoSimpleRowTracking(GetSimple(_inlineKey, _inlineEntityType), _inlineInstance!, _inlineKeyValues!);
        _deferring = false;
        ClearInline();
        return deferred;
    }

    /// <summary>Records an include's <c>SetIsLoaded</c> for replay when its entity's tracking is deferred.</summary>
    internal void RecordLoaded(object entity, INavigation navigation)
        => (_pending ?? Promote()).RecordLoaded(entity, navigation);

    /// <inheritdoc />
    public override InternalEntityEntry? TryGetEntry(IKey key, object[] keyValues, bool throwOnNullKey, out bool hasNullKey)
    {
        if (!_deferring)
        {
            return base.TryGetEntry(key, keyValues, throwOnNullKey, out hasNullKey);
        }

        // A null key part is answered by EF itself (it throws, or reports hasNullKey, and finds nothing); nothing else is looked up now,
        // because the row is only tracked, and so only resolved against what is tracked, when the enumerator reaches it.
        if (Array.IndexOf(keyValues, null) >= 0)
        {
            return base.TryGetEntry(key, keyValues, throwOnNullKey, out hasNullKey);
        }

        if (_pending == null && _inlineKey == null)
        {
            _inlineKey = key;
            _inlineKeyValues = keyValues;
            _inlineThrowOnNullKey = throwOnNullKey;
        }
        else
        {
            (_pending ?? Promote()).Begin(key, keyValues, throwOnNullKey);
        }

        hasNullKey = false;
        return null;
    }

#if EF8
    /// <inheritdoc />
    public override InternalEntityEntry StartTracking(IEntityType entityType, object entity, ValueBuffer valueBuffer)
    {
        if (!_deferring)
        {
            return base.StartTracking(entityType, entity, valueBuffer);
        }

        DeferTracking(entityType, entity, valueBuffer.IsEmpty, valueBuffer);
        return null!;
    }

    internal static ValueBuffer EmptyShadowValues => ValueBuffer.Empty;

    internal InternalEntityEntry ReplayStartTracking(IEntityType entityType, object entity, ValueBuffer valueBuffer)
        => base.StartTracking(entityType, entity, valueBuffer);

    private void DeferTracking(IEntityType entityType, object entity, bool noShadowValues, ValueBuffer shadowValues)
#else
    /// <inheritdoc />
    public override InternalEntityEntry StartTracking(IEntityType entityType, object entity, in ISnapshot snapshot)
    {
        if (!_deferring)
        {
            return base.StartTracking(entityType, entity, snapshot);
        }

        DeferTracking(entityType, entity, ReferenceEquals(snapshot, Snapshot.Empty), snapshot);
        return null!;
    }

    internal static ISnapshot EmptyShadowValues => Snapshot.Empty;

    internal InternalEntityEntry ReplayStartTracking(IEntityType entityType, object entity, ISnapshot snapshot)
        => base.StartTracking(entityType, entity, snapshot);

    private void DeferTracking(IEntityType entityType, object entity, bool noShadowValues, ISnapshot shadowValues)
#endif
    {
        if (_pending == null && _inlineKey != null && _inlineEntityType == null && noShadowValues)
        {
            _inlineEntityType = entityType;
            _inlineInstance = entity;
            return;
        }

        (_pending ?? Promote()).Complete(entityType, entity, shadowValues);
    }

    // Moves the row's inline record, if any, into a full MongoPendingTracking.
    private MongoPendingTracking Promote()
    {
        var pending = _pending = new MongoPendingTracking();
        if (_inlineKey != null)
        {
            pending.Begin(_inlineKey, _inlineKeyValues!, _inlineThrowOnNullKey);
            if (_inlineEntityType != null)
            {
                pending.Complete(_inlineEntityType, _inlineInstance!, EmptyShadowValues);
            }

            ClearInline();
        }

        return pending;
    }

    private void ClearInline()
    {
        _inlineKey = null;
        _inlineKeyValues = null;
        _inlineEntityType = null;
        _inlineInstance = null;
    }

    private MongoSimpleTracking GetSimple(IKey key, IEntityType entityType)
    {
        if (_lastSimple is { } last && last.EntityType == entityType)
        {
            return last;
        }

        _simpleByType ??= [];
        if (!_simpleByType.TryGetValue(entityType, out var simple))
        {
            _simpleByType[entityType] = simple = new MongoSimpleTracking(key, entityType);
        }

        return _lastSimple = simple;
    }

    internal InternalEntityEntry? ReplayTryGetEntry(IKey key, object[] keyValues, bool throwOnNullKey, out bool hasNullKey)
        => base.TryGetEntry(key, keyValues, throwOnNullKey, out hasNullKey);
}
