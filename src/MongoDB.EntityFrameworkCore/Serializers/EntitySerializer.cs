﻿/* Copyright 2023-present MongoDB Inc.
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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson.Serialization;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Serializers
{
    /// <summary>
    /// Provides the interface between the EFCore <see cref="IReadOnlyEntityType"/> metadata
    /// and the MongoDB LINQ provider's <see cref="IBsonDocumentSerializer"/> interface.
    /// </summary>
    /// <typeparam name="TValue">The underlying CLR type being handled by this serializer.</typeparam>
    internal class EntitySerializer<TValue> : IBsonSerializer<TValue>, IBsonDocumentSerializer
    {
        private readonly Func<IReadOnlyProperty, bool> _isStored = p => !p.IsShadowProperty() && p.GetElementName() != "";
        private readonly IReadOnlyEntityType _entityType;
        private readonly EntitySerializerCache _entitySerializerCache;

        public EntitySerializer(IReadOnlyEntityType entityType, EntitySerializerCache entitySerializerCache)
        {
            ArgumentNullException.ThrowIfNull(entityType);
            ArgumentNullException.ThrowIfNull(entitySerializerCache);

            _entityType = entityType;
            _entitySerializerCache = entitySerializerCache;
        }

        public Type ValueType => typeof(TValue);

        public TValue Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
            => throw new NotImplementedException();

        object? IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
            => Deserialize(context, args);

        public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TValue value)
        {
            if (value == null)
            {
                context.Writer.WriteNull();
                return;
            }

            // We do not support direct entity serialization right now because:
            //  - Matching owned entities by example or expression may mismatch because of unmapped fields/default values
            //    (we will likely configurable policy for this)
            //  - Matching root/entities with keys should be done by key fields, not by entity comparison
            //    (we will rewrite the expression tree to do this automatically in a future update)

            var storedKeyProperties = GetStoredKeyProperties();
            var uniqueness = storedKeyProperties.Any()
                ? string.Join(", ", storedKeyProperties.Select(p => "'" + p.Name + "'"))
                : "unique fields";

            throw new NotSupportedException($"Entity to entity comparison is not supported. Compare '{_entityType.DisplayName()}' entities by {uniqueness} instead.");
        }

        private IReadOnlyProperty[] GetStoredKeyProperties()
            => _entityType.FindPrimaryKey()?.Properties.Where(_isStored).ToArray() ?? [];

        void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value)
            => Serialize(context, args, (TValue)value);

        public bool TryGetMemberSerializationInfo(string memberName, out BsonSerializationInfo? serializationInfo)
        {
            var property = _entityType.FindProperty(memberName);
            if (property != null)
            {
                serializationInfo = SerializationHelper.GetPropertySerializationInfo(property);
                return true;
            }

            var navigation = _entityType.FindNavigation(memberName);
            if (navigation != null)
            {
                var entityType = navigation.TargetEntityType;
                var serializer = navigation.IsCollection
                    ? new CollectionSerializationProvider().GetSerializer(navigation.ClrType)
                    : _entitySerializerCache.GetOrCreateSerializer(entityType);
                var elementName = entityType.GetContainingElementName();
                if (elementName != null)
                {
                    serializationInfo = new BsonSerializationInfo(elementName, serializer, entityType.ClrType);
                    return true;
                }
            }

            serializationInfo = default;
            return false;
        }
    }
}
