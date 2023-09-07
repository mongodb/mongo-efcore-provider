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

namespace MongoDB.EntityFrameworkCore.Serializers
{
    internal class EntitySerializer<TValue> : IBsonSerializer<TValue>, IBsonDocumentSerializer
    {
        private readonly IReadOnlyEntityType _entityType;

        public EntitySerializer(IReadOnlyEntityType entityType)
        {
            ArgumentNullException.ThrowIfNull(entityType);
            _entityType = entityType;
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

            // TODO: handle navigation properties also?

            serializationInfo = default;
            return false;
        }
    }
}
