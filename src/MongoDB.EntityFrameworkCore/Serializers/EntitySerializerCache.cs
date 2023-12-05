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
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using MongoDB.Bson.Serialization;

namespace MongoDB.EntityFrameworkCore.Serializers;

/// <summary>
/// Provides a cache of <see cref="EntitySerializer{TValue}"/> objects that can be re-used as required when serializing
/// <see cref="EntityType"/> to Bson documents.
/// </summary>
public sealed class EntitySerializerCache
{
    private readonly ConcurrentDictionary<IReadOnlyEntityType, IBsonSerializer> _cache = new();

    public IBsonSerializer GetOrCreateSerializer(IReadOnlyEntityType entityType) =>
        _cache.GetOrAdd(entityType,
            key => (IBsonSerializer)Activator.CreateInstance(typeof(EntitySerializer<>).MakeGenericType(key.ClrType), key, this)!);
}
