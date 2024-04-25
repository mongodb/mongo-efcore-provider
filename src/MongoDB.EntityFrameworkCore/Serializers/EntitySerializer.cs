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
            => throw new NotImplementedException();

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
                var serializer = _entitySerializerCache.GetOrCreateSerializer(entityType);
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
