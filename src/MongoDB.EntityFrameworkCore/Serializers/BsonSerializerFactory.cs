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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Serializers;

/// <summary>
/// Provides the correct <see cref="IBsonSerializer"/> for a given entity, property or CLR type.
/// </summary>
public sealed class BsonSerializerFactory
{
    private static readonly Type[] SupportedDictionaryTypes =
        [typeof(Dictionary<,>), typeof(IDictionary<,>), typeof(IReadOnlyDictionary<,>), typeof(ReadOnlyDictionary<,>)];

    private static bool SupportsDictionary(Type type)
        => type.IsGenericType && SupportedDictionaryTypes.Contains(type.GetGenericTypeDefinition());

    private readonly ConcurrentDictionary<IReadOnlyEntityType, IBsonSerializer> _entitySerializersCache = new();

    internal IBsonSerializer GetEntitySerializer(IReadOnlyEntityType entityType) =>
        _entitySerializersCache.GetOrAdd(entityType, CreateEntitySerializer);

    internal IBsonSerializer CreateEntitySerializer(IReadOnlyEntityType entityType) =>
        CreateGenericSerializer(typeof(EntitySerializer<>), [entityType.ClrType], entityType, this);

    internal static IBsonSerializer CreateTypeSerializer(Type type, IReadOnlyProperty? property = null)
        => type switch
        {
            _ when type == typeof(bool) => BooleanSerializer.Instance,
            _ when type == typeof(byte) => new ByteSerializer(),
            _ when type == typeof(char) => new CharSerializer(),
            _ when type == typeof(DateTime) => GetDateTimeSerializer(property),
            _ when type == typeof(DateTimeOffset) => new DateTimeOffsetSerializer(),
            _ when type == typeof(DateOnly) => DateOnlySerializer.Instance,
            _ when type == typeof(TimeOnly) => TimeOnlySerializer.Instance,
            _ when type == typeof(decimal) => new DecimalSerializer(),
            _ when type == typeof(double) => DoubleSerializer.Instance,
            _ when type == typeof(Guid) => GuidSerializer.StandardInstance,
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
            _ when type == typeof(byte[]) => new ByteArraySerializer(),
            _ when type.IsEnum => EnumSerializer.Create(type),
            {IsArray: true}
                => GetArraySerializer(type, CreateTypeSerializer(type.GetElementType()!)),
            {IsGenericType: true} when type.GetGenericTypeDefinition() == typeof(Nullable<>)
                => GetNullableSerializer(type.GetGenericArguments()[0], property),
            {IsGenericType: true} when SupportsDictionary(type)
                => GetDictionarySerializer(type),
            {IsGenericType: true}
                => GetCollectionSerializer(type, CreateTypeSerializer(type.GetGenericArguments()[0])),

            _ => throw new NotSupportedException($"No known serializer for type '{type.ShortDisplayName()}'.")
        };


    internal static IBsonSerializer CreateTypeSerializer(IReadOnlyProperty property)
    {
        if (property.FindTypeMapping() is {Converter: { } converter})
        {
            return CreateValueConverterSerializer(converter, property);
        }

        var serializer = CreateTypeSerializer(property.ClrType, property);

        return property.GetBsonRepresentation() is { } bsonRepresentation
            ? ApplyBsonRepresentation(bsonRepresentation, serializer)
            : serializer;
    }

    private static IBsonSerializer CreateValueConverterSerializer(ValueConverter converter, IReadOnlyProperty property)
    {
        if (converter.ModelClrType.IsNullableValueType())
        {
            throw new NotSupportedException(
                $"Unsupported ValueConverter for Nullable<{converter.ModelClrType.UnwrapNullableType().Name
                }> encountered. Null conversion must be left to EF Core. "
                + $"If using HasConversion with conversion expressions directly move them to constructor arguments of a ValueConverter instead. "
                + $"For example: mb.Entity<{property.DeclaringType.DisplayName()}>().Property(e => e.{property.Name
                }).HasConversion(x => x, y => y) becomes .HasConversion(new ValueConverter(x => x, y => y));");
        }

        var typeSerializer = CreateTypeSerializer(converter.ProviderClrType);

        if (property.GetBsonRepresentation() is { } bsonRepresentation)
        {
            typeSerializer = ApplyBsonRepresentation(bsonRepresentation, typeSerializer);
        }

        var valueConverterSerializerType = typeof(ValueConverterSerializer<,>)
            .MakeGenericType(converter.ModelClrType, converter.ProviderClrType);

        return (IBsonSerializer?)Activator.CreateInstance(valueConverterSerializerType, converter, typeSerializer)
               ?? throw new InvalidOperationException($"Unable to create '{valueConverterSerializerType.ShortDisplayName()}'.");
    }

    private static IBsonSerializer ApplyBsonRepresentation(
        BsonRepresentationConfiguration representation,
        IBsonSerializer typeSerializer)
    {
        if (typeSerializer is INullableSerializer nullableSerializer)
        {
            var valueSerializer = ApplyBsonRepresentation(representation, nullableSerializer.ValueSerializer);
            return NullableSerializer.Create(valueSerializer);
        }

        if (typeSerializer is not IRepresentationConfigurable representationConfigurable)
        {
            return typeSerializer;
        }

        var representationTypeSerializer = representationConfigurable.WithRepresentation(representation.BsonType);
        if (representationTypeSerializer is not IRepresentationConverterConfigurable converterConfigurable)
        {
            return representationTypeSerializer;
        }

        var allowOverflow = representation.AllowOverflow ?? false;
        var allowTruncation = representation.AllowTruncation ?? representation.BsonType == BsonType.Decimal128;
        return converterConfigurable.WithConverter(new RepresentationConverter(allowOverflow, allowTruncation));
    }

    private static DateTimeSerializer GetDateTimeSerializer(IReadOnlyProperty? property)
        => property?.GetDateTimeKind() switch
        {
            DateTimeKind.Local => DateTimeSerializer.LocalInstance,
            DateTimeKind.Utc => DateTimeSerializer.UtcInstance,
            _ => DateTimeSerializer.Instance
        };

    private static IBsonSerializer GetNullableSerializer(Type elementType, IReadOnlyProperty? property)
        => CreateGenericSerializer(typeof(NullableSerializer<>), [elementType], CreateTypeSerializer(elementType, property));

    private static IBsonSerializer GetArraySerializer(Type type, IBsonSerializer childSerializer)
    {
        if (type.GetArrayRank() != 1)
        {
            throw new NotSupportedException($"Unsupported multi-dimensional array type '{type.ShortDisplayName()
            }'. Only single-dimension arrays are supported.");
        }

        return CreateGenericSerializer(typeof(ArraySerializer<>), [type.GetElementType()!], childSerializer);
    }

    internal IBsonSerializer GetNavigationSerializer(IReadOnlyNavigation navigation)
        => navigation.IsCollection
            ? GetCollectionSerializer(navigation.ClrType, GetEntitySerializer(navigation.TargetEntityType))
            : GetEntitySerializer(navigation.TargetEntityType);

    private static IBsonSerializer GetCollectionSerializer(Type type, IBsonSerializer childSerializer)
    {
        var enumerableInterface = type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerableInterface == null)
        {
            throw new NotSupportedException($"Unsupported collection type '{type.ShortDisplayName()}'.");
        }

        var itemType = enumerableInterface.GetTypeInfo().GetGenericArguments()[0];

        var readOnlyCollectionType = typeof(ReadOnlyCollection<>).MakeGenericType(itemType);
        if (type == readOnlyCollectionType)
        {
            return CreateGenericSerializer(typeof(ReadOnlyCollectionSerializer<>), [itemType], childSerializer);
        }

        if (readOnlyCollectionType.GetTypeInfo().IsAssignableFrom(type))
        {
            return CreateGenericSerializer(typeof(ReadOnlyCollectionSubclassSerializer<,>), [type, itemType], childSerializer);
        }

        if (type.IsInterface)
        {
            var listType = typeof(List<>).MakeGenericType(itemType);
            if (type.IsAssignableFrom(listType))
            {
                return CreateGenericSerializer(typeof(IEnumerableDeserializingAsCollectionSerializer<,,>),
                    [type, itemType, listType], childSerializer);
            }
        }

        return CreateGenericSerializer(typeof(EnumerableInterfaceImplementerSerializer<,>), [type, itemType], childSerializer);
    }

    internal static BsonSerializationInfo GetPropertySerializationInfo(IReadOnlyProperty property)
    {
        var serializer = CreateTypeSerializer(property);

        if (property.IsPrimaryKey() && property.DeclaringType is IEntityType entityType
                                    && entityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return BsonSerializationInfo.CreateWithPath(
                ["_id", property.GetElementName()], serializer, serializer.ValueType);
        }

        return new BsonSerializationInfo(property.GetElementName(), serializer, serializer.ValueType);
    }

    private static IBsonSerializer GetDictionarySerializer(Type type)
    {
        var genericTypeDefinition = type.GetGenericTypeDefinition();

        var dictionaryInterface = genericTypeDefinition == typeof(IDictionary<,>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (dictionaryInterface != null)
        {
            var genericArguments = dictionaryInterface.GetTypeInfo().GetGenericArguments();
            var keyType = genericArguments[0];
            var valueType = genericArguments[1];
            var concreteType = type.IsInterface
                ? typeof(Dictionary<,>).MakeGenericType(keyType, valueType)
                : type;
            return CreateGenericSerializer(typeof(DictionaryInterfaceImplementerSerializer<,,>),
                [concreteType, keyType, valueType]);
        }

        var readOnlyDictionaryInterface = genericTypeDefinition == typeof(IReadOnlyDictionary<,>)
            ? type
            : type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

        if (readOnlyDictionaryInterface != null)
        {
            var genericArguments = readOnlyDictionaryInterface.GetTypeInfo().GetGenericArguments();
            var keyType = genericArguments[0];
            var valueType = genericArguments[1];
            var concreteType = type.IsInterface
                ? typeof(ReadOnlyDictionary<,>).MakeGenericType(keyType, valueType)
                : type;
            return CreateGenericSerializer(typeof(ReadOnlyDictionaryInterfaceImplementerSerializer<,,>),
                [concreteType, keyType, valueType]);
        }

        throw new NotSupportedException($"Unsupported dictionary type '{type.ShortDisplayName()}'.");
    }

    private static IBsonSerializer CreateGenericSerializer(Type serializer, Type[] genericArgs, params object[] constructorArgs)
        => (IBsonSerializer)Activator.CreateInstance(serializer.MakeGenericType(genericArgs), constructorArgs)!;
}
