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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers;

internal static class SerializationHelper
{
    public static T GetPropertyValue<T>(BsonDocument document, IReadOnlyProperty property)
    {
        var serializationInfo = GetPropertySerializationInfo(property);
        if(TryReadElementValue(document, serializationInfo, out T value) || property.IsNullable)
        {
            return value;
        }

        throw new KeyNotFoundException($"Document does not contain value for non-nullable field '{property.Name}'.");
    }

    public static T GetElementValue<T>(BsonDocument document, string elementName)
    {
        var serializationInfo = new BsonSerializationInfo(elementName, CreateTypeSerializer(typeof(T)), typeof(T));
        if(TryReadElementValue(document, serializationInfo, out T value) || typeof(T).IsNullableType())
        {
            return value;
        }

        throw new KeyNotFoundException($"Document does not contain value for non-nullable field '{elementName}'.");
    }

    internal static void WriteKeyProperties(IBsonWriter writer, IUpdateEntry entry)
    {
        var keyProperties = entry.EntityType.FindPrimaryKey()
            .Properties
            .Where(p => !p.IsShadowProperty() && p.GetElementName() != "").ToArray();

        if (!keyProperties.Any()) return;

        bool compoundKey = keyProperties.Length > 1;
        if (compoundKey)
        {
            writer.WriteName("_id");
            writer.WriteStartDocument();
        }

        foreach (var property in keyProperties)
        {
            object? propertyValue = entry.GetCurrentValue(property);
            var serializationInfo = GetPropertySerializationInfo(property);
            string? elementName = serializationInfo.ElementPath?.Last() ?? serializationInfo.ElementName;
            WriteProperty(writer, elementName, propertyValue, serializationInfo.Serializer);
        }

        if (compoundKey)
        {
            writer.WriteEndDocument();
        }
    }

    internal static void WriteNonKeyProperties(IBsonWriter writer, IUpdateEntry entry, Func<IProperty, bool>? propertyFilter = null)
    {
        var properties = entry.EntityType.GetProperties()
            .Where(p => !p.IsShadowProperty() && !p.IsPrimaryKey() && p.GetElementName() != "")
            .Where(p => propertyFilter == null || propertyFilter(p))
            .ToArray();

        foreach (var property in properties)
        {
            var propertyValue = entry.GetCurrentValue(property);
            var serializationInfo = GetPropertySerializationInfo(property);
            WriteProperty(writer, serializationInfo.ElementName, propertyValue, serializationInfo.Serializer);
        }
    }

    private static void WriteProperty(IBsonWriter writer, string elementName, object value, IBsonSerializer serializer)
    {
        writer.WriteName(elementName);
        var context = BsonSerializationContext.CreateRoot(writer);
        serializer.Serialize(context, value);
    }

    internal static BsonSerializationInfo GetPropertySerializationInfo(IReadOnlyProperty property)
    {
        var serializer = CreateTypeSerializer(property.ClrType, property);
        if (property.IsPrimaryKey() && property.DeclaringEntityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return BsonSerializationInfo.CreateWithPath(new[] {"_id", property.GetElementName()}, serializer, property.ClrType);
        }

        return new BsonSerializationInfo(property.GetElementName(), serializer, property.ClrType);
    }

    private static IBsonSerializer CreateTypeSerializer(Type type, IReadOnlyProperty? property = null)
    {
        bool isNullable = type.IsNullableValueType();
        if (isNullable)
        {
            type = Nullable.GetUnderlyingType(type);
        }

        var serializer = type switch
        {
            var t when t == typeof(bool) => BooleanSerializer.Instance,
            var t when t == typeof(byte) => new ByteSerializer(),
            var t when t == typeof(char) => new CharSerializer(),
            var t when t == typeof(DateTime) => CreateDateTimeSerializer(property),
            var t when t == typeof(DateTimeOffset) => new DateTimeOffsetSerializer(),
            var t when t == typeof(decimal) => new DecimalSerializer(),
            var t when t == typeof(double) => DoubleSerializer.Instance,
            // TODO: investigate what should be as default here,
            // Switched to new GuidSerializer() instead of GuidSerializer.StandardInstance to make tests happy
            var t when t == typeof(Guid) => new GuidSerializer(),
            var t when t == typeof(short) => new Int16Serializer(),
            var t when t == typeof(int) => Int32Serializer.Instance,
            var t when t == typeof(long) => Int64Serializer.Instance,
            var t when t == typeof(ObjectId) => ObjectIdSerializer.Instance,
            var t when t == typeof(TimeSpan) => new TimeSpanSerializer(),
            var t when t == typeof(sbyte) => new SByteSerializer(),
            var t when t == typeof(float) => new SingleSerializer(),
            var t when t == typeof(string) => new StringSerializer(),
            var t when t == typeof(ushort) => new UInt16Serializer(),
            var t when t == typeof(uint) => new UInt32Serializer(),
            var t when t == typeof(ulong) => new UInt64Serializer(),
            var t when t == typeof(Decimal128) => new Decimal128Serializer(),
            var t when t.IsEnum => EnumSerializer.Create(t),
            {IsArray: true} a => CreateArraySerializer(a.GetElementType()),
            {IsGenericType: true} l => CreateListSerializer(l.TryGetItemType(typeof(IEnumerable<>))),
            _ => throw new NotSupportedException($"Cannot resolve Serializer for '{type.FullName}' type."),
        };

        if (isNullable)
        {
            serializer = NullableSerializer.Create(serializer);
        }

        return serializer;
    }

    private static IBsonSerializer CreateDateTimeSerializer(IReadOnlyProperty? property)
    {
        var dateTimeKind = property?.GetDateTimeKind() ?? DateTimeKind.Unspecified;
        if (dateTimeKind == DateTimeKind.Unspecified)
        {
            return new DateTimeSerializer();
        }

        return new DateTimeSerializer(dateTimeKind);
    }

    private static IBsonSerializer CreateArraySerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(ArraySerializer<>).MakeGenericType(elementType));

    private static IBsonSerializer CreateListSerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(ListSerializer<>).MakeGenericType(elementType));

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
