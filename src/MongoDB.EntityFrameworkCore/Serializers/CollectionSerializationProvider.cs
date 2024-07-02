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
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers;

internal class CollectionSerializationProvider : BsonSerializationProviderBase
{
    public static readonly CollectionSerializationProvider Instance = new();

    public override IBsonSerializer GetSerializer(Type type, IBsonSerializerRegistry serializerRegistry)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1)
            {
                throw new NotSupportedException($"Unsupported multi-dimensional array type '{type.ShortDisplayName()
                }'. Only single-dimension arrays are supported.");
            }

            return CreateGenericSerializer(typeof(ArraySerializer<>), [type.GetElementType()!], serializerRegistry);
        }

        if (type is {IsGenericType: true, ContainsGenericParameters: true})
        {
            throw new ArgumentException($"Generic type '{type.ShortDisplayName()}' has unassigned generic type parameters.",
                nameof(type));
        }

        return CreateCollectionSerializer(type, serializerRegistry)
               ?? throw new ArgumentException($"No known serializer for type '{type.ShortDisplayName()}'.", nameof(type));
    }

    private IBsonSerializer? CreateCollectionSerializer(Type type, IBsonSerializerRegistry serializerRegistry)
    {
        var enumerableInterface = type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerableInterface == null) return null;

        var itemType = enumerableInterface.GetTypeInfo().GetGenericArguments()[0];

        var readOnlyCollectionType = typeof(ReadOnlyCollection<>).MakeGenericType(itemType);
        if (type == readOnlyCollectionType)
        {
            return CreateGenericSerializer(typeof(ReadOnlyCollectionSerializer<>), [itemType], serializerRegistry);
        }

        if (readOnlyCollectionType.GetTypeInfo().IsAssignableFrom(type))
        {
            return CreateGenericSerializer(typeof(ReadOnlyCollectionSubclassSerializer<,>), [type, itemType], serializerRegistry);
        }

        if (type.IsInterface)
        {
            var listType = typeof(List<>).MakeGenericType(itemType);
            if (type.IsAssignableFrom(listType))
            {
                return CreateGenericSerializer(typeof(IEnumerableDeserializingAsCollectionSerializer<,,>),
                    [type, itemType, listType], serializerRegistry);
            }
        }

        return CreateGenericSerializer(typeof(EnumerableInterfaceImplementerSerializer<,>), [type, itemType], serializerRegistry);
    }
}
