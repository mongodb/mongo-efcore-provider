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
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers;

internal static class SerializationHelper
{
    public static T GetPropertyValue<T>(BsonDocument document, IReadOnlyPropertyBase property)
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

        foreach (var property in properties)
        {
            var propertyValue = entry.GetCurrentValue(property);
            var propertySerializationInfo = GetPropertySerializationInfo(property);
            // TODO: Add ElementPath support here
            var elementName = propertySerializationInfo.ElementName;
            writer.WriteName(elementName);

            var propertySerializer = propertySerializationInfo.Serializer;
            var context = BsonSerializationContext.CreateRoot(writer);
            propertySerializer.Serialize(context, propertyValue);
        }

        writer.WriteEndDocument();
    }

    public static BsonSerializationInfo GetPropertySerializationInfo(IReadOnlyPropertyBase property)
        // TODO: extend this method with primary key composition logic
        => new BsonSerializationInfo(property.GetElementName(), CreateTypeSerializer(property.ClrType), property.ClrType);

    private static IBsonSerializer CreateTypeSerializer(Type type)
    {
        return type switch
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
            {IsArray: true} a => CreateArraySerializer(a.GetElementType()),
            {IsGenericType:true} l => CreateListSerializer(l.TryGetItemType(typeof(IEnumerable<>))),
            // TODO: doubt if having BsonDocumentSerializer here is a good idea or not, needed when we have projection that involves sub-documents
            var t when t == typeof(BsonDocument) => BsonDocumentSerializer.Instance,
            _ => throw new NotSupportedException($"Cannot resolve Serializer for '{type.FullName}' type."),
        };
    }

    private static IBsonSerializer CreateArraySerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(ArraySerializer<>).MakeGenericType(elementType));

    private static IBsonSerializer CreateListSerializer(Type elementType)
        => (IBsonSerializer)Activator.CreateInstance(typeof(ListSerializer<>).MakeGenericType(elementType));

    private static T ReadElementValue<T>(BsonDocument document, BsonSerializationInfo elementSerializationInfo)
    {
        // TODO: decide what to do with non-existing elements
        // TODO: support ElementPath here
        var value = document.GetValue(elementSerializationInfo.ElementName);

        return (T)elementSerializationInfo.DeserializeValue(value);
    }
}
