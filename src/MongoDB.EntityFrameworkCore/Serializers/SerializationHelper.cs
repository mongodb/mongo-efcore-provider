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

namespace MongoDB.EntityFrameworkCore.Serializers;

internal static class SerializationHelper
{
    internal static BsonSerializationInfo GetPropertySerializationInfo(IReadOnlyProperty property)
    {
        var serializer = CreateTypeSerializer(property.ClrType, property);
        if (property.IsPrimaryKey() && property.DeclaringEntityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return BsonSerializationInfo.CreateWithPath(new[] {"_id", property.GetElementName()}, serializer, property.ClrType);
        }

        return new BsonSerializationInfo(property.GetElementName(), serializer, property.ClrType);
    }

    internal static IBsonSerializer CreateTypeSerializer(Type? type, IReadOnlyProperty? property = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.IsNullableValueType())
        {
            return NullableSerializer.Create(CreateTypeSerializer(Nullable.GetUnderlyingType(type), property));
        }

        return type switch
        {
            _ when type == typeof(bool) => BooleanSerializer.Instance,
            _ when type == typeof(byte) => new ByteSerializer(),
            _ when type == typeof(char) => new CharSerializer(),
            _ when type == typeof(DateTime) => CreateDateTimeSerializer(property),
            _ when type == typeof(DateTimeOffset) => new DateTimeOffsetSerializer(),
            _ when type == typeof(decimal) => new DecimalSerializer(),
            _ when type == typeof(double) => DoubleSerializer.Instance,
            _ when type == typeof(Guid) => new GuidSerializer(),
            _ when type == typeof(short) => new Int16Serializer(),
            _ when type == typeof(int) => Int32Serializer.Instance,
            _ when type == typeof(long) => Int64Serializer.Instance,
            _ when type == typeof(ObjectId) => ObjectIdSerializer.Instance,
            _ when type == typeof(TimeSpan) => new TimeSpanSerializer(),
            _ when type == typeof(sbyte) => new SByteSerializer(),
            _ when type == typeof(float) => new SingleSerializer(),
            _ when type == typeof(string) => new StringSerializer(),
            _ when type == typeof(ushort) => new UInt16Serializer(),
            _ when type == typeof(uint) => new UInt32Serializer(),
            _ when type == typeof(ulong) => new UInt64Serializer(),
            _ when type == typeof(Decimal128) => new Decimal128Serializer(),
            _ when type.IsEnum => EnumSerializer.Create(type),
            {IsArray: true} => CreateArraySerializer(type.GetElementType()),
            {IsGenericType: true} => CreateListSerializer(type.TryGetItemType(typeof(IEnumerable<>))),
            _ => throw new NotSupportedException($"Cannot resolve serializer for '{type.FullName}' type."),
        };
    }

    private static IBsonSerializer CreateDateTimeSerializer(IReadOnlyProperty? property)
    {
        var dateTimeKind = property?.GetDateTimeKind() ?? DateTimeKind.Unspecified;
        return dateTimeKind == DateTimeKind.Unspecified ? new DateTimeSerializer() : new DateTimeSerializer(dateTimeKind);
    }

    private static IBsonSerializer CreateArraySerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(ArraySerializer<>).MakeGenericType(elementType));

    internal static IBsonSerializer CreateListSerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(ListSerializer<>).MakeGenericType(elementType));
}
