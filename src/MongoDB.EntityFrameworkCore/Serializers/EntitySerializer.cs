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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers
{
    internal static class EntitySerializer
    {
        public static IBsonSerializer Create(IReadOnlyEntityType entityType)
        {
            var clrType = entityType.ClrType;
            var serializerType = typeof(EntitySerializer<>).MakeGenericType(clrType);
            return (IBsonSerializer)Activator.CreateInstance(serializerType, entityType)!;
        }
    }

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
                var elementName = property.GetElementName();
                var serializer = CreatePropertySerializer(property);
                serializationInfo = new BsonSerializationInfo(elementName, serializer, property.ClrType);
                return true;
            }

            // TODO: handle navigation properties also?

            serializationInfo = default;
            return false;
        }

        private IBsonSerializer CreatePropertySerializer(IReadOnlyProperty property)
        {
            return property.ClrType switch
            {
                var t when t == typeof(bool) => BooleanSerializer.Instance,
                var t when t == typeof(byte) => new ByteSerializer(),
                var t when t == typeof(char) => new CharSerializer(),
                var t when t == typeof(DateTime) => new DateTimeSerializer(),
                var t when t == typeof(DateTimeOffset) => new DateTimeOffsetSerializer(),
                var t when t == typeof(decimal) => new DecimalSerializer(),
                var t when t == typeof(double) => DoubleSerializer.Instance,
                var t when t == typeof(Guid) => GuidSerializer.StandardInstance,
                var t when t == typeof(short) => new Int16Serializer(),
                var t when t == typeof(int) => Int32Serializer.Instance,
                var t when t == typeof(long) => Int64Serializer.Instance,
                var t when t == typeof(ObjectId) => ObjectIdSerializer.Instance,
                var t when t == typeof(sbyte) => new SByteSerializer(),
                var t when t == typeof(float) => new SingleSerializer(),
                var t when t == typeof(string) => new StringSerializer(),
                var t when t == typeof(ushort) => new UInt16Serializer(),
                var t when t == typeof(uint) => new UInt32Serializer(),
                var t when t == typeof(ulong) => new UInt64Serializer(),
                var t when t == typeof(Decimal128) => new Decimal128Serializer(),
                {IsArray: true} a => CreateArraySerializer(a.GetElementType()),
                {IsGenericType:true} l => CreateListSerializer(l.TryGetItemType(typeof(IEnumerable<>))),
                _ => throw new Exception($"Don't know how property {property.Name} of type {property.ClrType} should be serialized.")
            };
        }

        private static IBsonSerializer CreateArraySerializer(Type elementType)
            => (IBsonSerializer)Activator.CreateInstance(typeof(ArraySerializer<>).MakeGenericType(elementType));

        private static IBsonSerializer CreateListSerializer(Type elementType)
            => (IBsonSerializer)Activator.CreateInstance(typeof(ListSerializer<>).MakeGenericType(elementType));
    }
}
