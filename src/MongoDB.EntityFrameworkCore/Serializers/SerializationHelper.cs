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
        return ReadElementValue<T>(document, serializationInfo);
    }

    public static T GetElementValue<T>(BsonDocument document, string elementName)
    {
        var serializationInfo = new BsonSerializationInfo(elementName, CreateTypeSerializer(typeof(T)), typeof(T));
        return ReadElementValue<T>(document, serializationInfo);
    }

    public static void WriteProperties(BsonDocument document, IUpdateEntry entry, IEnumerable<IProperty> properties)
    {
        using var writer = new BsonDocumentWriter(document);
        writer.WriteStartDocument();

        // Write PK first, including all primary key properties in case of composite key
        if (properties.Any(p => p.IsPrimaryKey()))
        {
            var pk = entry.EntityType.FindPrimaryKey();
            if (pk.Properties.Count > 1)
            {
                writer.WriteName("_id");
                writer.WriteStartDocument();
            }

            foreach (var property in pk.Properties)
            {
                var propertyValue = entry.GetCurrentValue(property);
                var serializationInfo = GetPropertySerializationInfo(property);
                var elementName = serializationInfo.ElementPath?.Last() ?? serializationInfo.ElementName;
                WriteProperty(writer, elementName, propertyValue, serializationInfo.Serializer);
            }

            if (pk.Properties.Count > 1)
            {
                writer.WriteEndDocument();
            }
        }

        foreach (var property in properties)
        {
            if (property.IsPrimaryKey())
            {
                continue;
            }

            var propertyValue = entry.GetCurrentValue(property);
            var serializationInfo = GetPropertySerializationInfo(property);
            WriteProperty(writer, serializationInfo.ElementName, propertyValue, serializationInfo.Serializer);
        }

        writer.WriteEndDocument();
        return;

        void WriteProperty(IBsonWriter writer, string elementName, object value, IBsonSerializer serializer)
        {
            writer.WriteName(elementName);
            var context = BsonSerializationContext.CreateRoot(writer);
            serializer.Serialize(context, value);
        }
    }

    public static BsonSerializationInfo GetPropertySerializationInfo(IReadOnlyProperty property)
    {
        var serializer = CreateTypeSerializer(property.ClrType);
        if (property.IsPrimaryKey() && property.DeclaringEntityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return BsonSerializationInfo.CreateWithPath(new[] { "_id", property.GetElementName()}, serializer, property.ClrType);
        }

        return new BsonSerializationInfo(property.GetElementName(), CreateTypeSerializer(property.ClrType), property.ClrType);
    }

    private static IBsonSerializer CreateTypeSerializer(Type type)
    {
        var isNullable = type.IsNullableValueType();
        if (isNullable)
        {
            type = Nullable.GetUnderlyingType(type);
        }

        var serializer = type switch
        {
            var t when t == typeof(bool) => BooleanSerializer.Instance,
            var t when t == typeof(byte) => new ByteSerializer(),
            var t when t == typeof(char) => new CharSerializer(),
            var t when t == typeof(DateTime) => new DateTimeSerializer(),
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
            var t when t == typeof(sbyte) => new SByteSerializer(),
            var t when t == typeof(float) => new SingleSerializer(),
            var t when t == typeof(string) => new StringSerializer(),
            var t when t == typeof(ushort) => new UInt16Serializer(),
            var t when t == typeof(uint) => new UInt32Serializer(),
            var t when t == typeof(ulong) => new UInt64Serializer(),
            var t when t == typeof(Decimal128) => new Decimal128Serializer(),
            var t when t.IsEnum => EnumSerializer.Create(t),
            {IsArray: true} a => CreateArraySerializer(a.GetElementType()),
            {IsGenericType:true} l => CreateListSerializer(l.TryGetItemType(typeof(IEnumerable<>))),
            // TODO: doubt if having BsonDocumentSerializer here is a good idea or not, needed when we have projection that involves sub-documents
            var t when t == typeof(BsonDocument) => BsonDocumentSerializer.Instance,
            _ => throw new NotSupportedException($"Cannot resolve Serializer for '{type.FullName}' type."),
        };

        if (isNullable)
        {
            serializer = NullableSerializer.Create(serializer);
        }

        return serializer;
    }

    private static IBsonSerializer CreateArraySerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(ArraySerializer<>).MakeGenericType(elementType));

    private static IBsonSerializer CreateListSerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(ListSerializer<>).MakeGenericType(elementType));

    private static T? ReadElementValue<T>(BsonDocument document, BsonSerializationInfo elementSerializationInfo)
    {
        // TODO: decide what to do with non-existing elements
        BsonValue rawValue;
        if (elementSerializationInfo.ElementPath == null)
        {
            rawValue = document.GetValue(elementSerializationInfo.ElementName);
        }
        else
        {
            rawValue = document;
            foreach (var node in elementSerializationInfo.ElementPath)
            {
                rawValue = ((BsonDocument)rawValue)[node];
                if (rawValue == null)
                {
                    return default;
                }
            }
        }

        return (T)elementSerializationInfo.DeserializeValue(rawValue);
    }
}
