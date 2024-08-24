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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

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

    public static T? GetElementValue<T>(BsonDocument document, string elementName)
    {
        var serializationInfo = new BsonSerializationInfo(elementName, BsonSerializerFactory.CreateTypeSerializer(typeof(T)), typeof(T));
        if (TryReadElementValue(document, serializationInfo, out T? value) || typeof(T).IsNullableType())
        {
            return value;
        }

        throw new InvalidOperationException($"Document element '{elementName}' is missing but required.");
    }

    internal static BsonSerializationInfo GetPropertySerializationInfo(IReadOnlyProperty property)
    {
        var serializer = BsonSerializerFactory.CreateTypeSerializer(property);

        if (property.IsPrimaryKey() && property.DeclaringType is IEntityType entityType
                                    && entityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return BsonSerializationInfo.CreateWithPath(new[]
            {
                "_id", property.GetElementName()
            }, serializer, serializer.ValueType);
        }

        return new BsonSerializationInfo(property.GetElementName(), serializer, serializer.ValueType);
    }

    private static bool TryReadElementValue<T>(BsonDocument document, BsonSerializationInfo elementSerializationInfo, out T? value)
    {
        BsonValue? rawValue;
        if (elementSerializationInfo.ElementPath == null)
        {
            document.TryGetValue(elementSerializationInfo.ElementName, out rawValue);
        }
        else
        {
            rawValue = document;
            foreach (var node in elementSerializationInfo.ElementPath)
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
