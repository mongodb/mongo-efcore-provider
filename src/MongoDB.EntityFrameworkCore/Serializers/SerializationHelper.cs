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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers;

internal static class SerializationHelper
{
    public static T? GetPropertyValue<T>(BsonDocument document, IReadOnlyProperty property)
    {
        var serializationInfo = GetPropertySerializationInfo(property);
        if (TryReadElementValue(document, serializationInfo, out T? value))
        {
            if (value == null && !property.IsNullable)
            {
                throw new InvalidOperationException($"Document element is null for required non-nullable property '{property.Name
                }'.");
            }

            return value;
        }

        if (property.IsNullable) return default;

        throw new InvalidOperationException($"Document element is missing for required non-nullable property '{property.Name}'.");
    }

    public static T GetElementValue<T>(BsonDocument document, string elementName)
    {
        var serializationInfo = new BsonSerializationInfo(elementName, CreateTypeSerializer(typeof(T)), typeof(T));
        if (TryReadElementValue(document, serializationInfo, out T value) || typeof(T).IsNullableType())
        {
            return value;
        }

        throw new InvalidOperationException($"Document element '{elementName}' is missing but required.");
    }

    internal static BsonSerializationInfo GetPropertySerializationInfo(IReadOnlyProperty property)
    {
        var serializer = CreateTypeSerializer(property.ClrType, property);
        if (property.IsPrimaryKey() && property.DeclaringEntityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return BsonSerializationInfo.CreateWithPath(new[]
            {
                "_id", property.GetElementName()
            }, serializer, property.ClrType);
        }

        return new BsonSerializationInfo(property.GetElementName(), serializer, property.ClrType);
    }

    private static IBsonSerializer CreateTypeSerializer(Type type, IReadOnlyProperty? property = null)
        => type switch
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
            {IsGenericType: true} when type.GetGenericTypeDefinition() == typeof(Nullable<>)
                => CreateNullableSerializer(type.GetGenericArguments()[0]),
            {IsGenericType: true} or {IsArray: true} => new ListSerializationProvider().GetSerializer(type),
            _ => throw new NotSupportedException($"No known serializer for type '{type.ShortDisplayName()}'."),
        };

    private static DateTimeSerializer CreateDateTimeSerializer(IReadOnlyProperty? property)
    {
        var dateTimeKind = property?.GetDateTimeKind() ?? DateTimeKind.Unspecified;
        return dateTimeKind == DateTimeKind.Unspecified
            ? new DateTimeSerializer()
            : new DateTimeSerializer(DateTimeKind.Local);
    }

    private static IBsonSerializer CreateNullableSerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(NullableSerializer<>).MakeGenericType(elementType))!;

    private static bool TryReadElementValue<T>(BsonDocument document, BsonSerializationInfo elementSerializationInfo, out T value)
    {
        BsonValue? rawValue;
        if (elementSerializationInfo.ElementPath == null)
        {
            document.TryGetValue(elementSerializationInfo.ElementName, out rawValue);
        }
        else
        {
            rawValue = document;
            foreach (string? node in elementSerializationInfo.ElementPath)
            {
                var doc = (BsonDocument)rawValue;
                if (!doc.TryGetValue(node, out rawValue))
                {
                    rawValue = null;
                    break;
                }
            }
        }

        if (rawValue != null)
        {
            value = (T)elementSerializationInfo.DeserializeValue(rawValue);
            return true;
        }

        value = default;
        return false;
    }
}
