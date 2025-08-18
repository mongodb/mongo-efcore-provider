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
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.ChangeTracking;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Create a <see cref="MongoTypeMapping"/> (or <see cref="CoreTypeMapping"/>) for
/// each property of an entity that should be mapped to the underlying MongoDB database.
/// </summary>
public class MongoTypeMappingSource(TypeMappingSourceDependencies dependencies)
    : TypeMappingSource(dependencies)
{
    private static readonly Type[] SupportedCollectionInterfaces =
    [
        typeof(IList<>),
        typeof(IReadOnlyList<>),
        typeof(IEnumerable<>)
    ];

    private static readonly Type[] SupportedDictionaryInterfaces =
    [
        typeof(IDictionary<,>),
        typeof(IReadOnlyDictionary<,>)
    ];

    /// <inheritdoc/>
    protected override CoreTypeMapping? FindMapping(in TypeMappingInfo mappingInfo)
    {
        if (mappingInfo.ClrType == null)
        {
            throw new InvalidOperationException($"Unable to determine CLR type for mappingInfo '{mappingInfo}'");
        }

        return FindPrimitiveMapping(mappingInfo)
               ?? FindCollectionMapping(mappingInfo)
               ?? base.FindMapping(mappingInfo);
    }

    private MongoTypeMapping? FindPrimitiveMapping(in TypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType!;

        if (clrType is {IsValueType: true}
            || clrType == typeof(string)
            || clrType == typeof(BinaryVectorFloat32)
            || clrType == typeof(BinaryVectorInt8)
            || clrType == typeof(BinaryVectorPackedBit)
            || clrType.TryGetItemType(typeof(ReadOnlyMemory<>)) != null
            || clrType.TryGetItemType(typeof(Memory<>)) != null)
        {
            return new MongoTypeMapping(clrType);
        }

        return null;
    }

    private MongoTypeMapping? FindCollectionMapping(in TypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType!;
        if (mappingInfo.ElementTypeMapping != null)
        {
            return null;
        }

        var elementType = clrType.TryGetItemType();
        if (elementType == null)
        {
            return null;
        }

        if (clrType.IsArray)
        {
            return CreateCollectionTypeMapping(clrType, elementType);
        }

        if (clrType is {IsGenericType: true, IsGenericTypeDefinition: false})
        {
            if (clrType.HasInterface(SupportedDictionaryInterfaces))
            {
                return CreateDictionaryTypeMapping(clrType);
            }

            if (clrType.HasInterface(SupportedCollectionInterfaces))
            {
                return CreateCollectionTypeMapping(clrType, elementType);
            }
        }

        return null;
    }

    private MongoTypeMapping? CreateCollectionTypeMapping(Type collectionType, Type elementType)
    {
        var elementMappingInfo = new TypeMappingInfo(elementType);
        var elementMapping = FindMapping(elementMappingInfo);
        return elementMapping == null
            ? null
            : new MongoTypeMapping(collectionType, CreateCollectionComparer(elementMapping, collectionType, elementType));
    }

    private static ValueComparer? CreateCollectionComparer(
        CoreTypeMapping elementMapping,
        Type collectionType,
        Type elementType)
    {
        var typeToInstantiate = FindCollectionTypeToInstantiate(collectionType, elementType);

        return (ValueComparer?)Activator.CreateInstance(
            elementType.IsNullableValueType()
                ? typeof(ListOfNullableValueTypesComparer<,>).MakeGenericType(typeToInstantiate,
                    elementType.UnwrapNullableType())
                : elementType.IsValueType
                    ? typeof(ListOfValueTypesComparer<,>).MakeGenericType(typeToInstantiate, elementType)
                    : typeof(ListOfReferenceTypesComparer<,>).MakeGenericType(typeToInstantiate, elementType),
            elementMapping.Comparer.ToNullableComparer(elementType)!);
    }

    private static Type FindCollectionTypeToInstantiate(Type collectionType, Type elementType)
    {
        if (collectionType.IsArray)
        {
            return collectionType;
        }

        var listOfT = typeof(List<>).MakeGenericType(elementType);

        if (collectionType.IsAssignableFrom(listOfT))
        {
            if (!collectionType.IsAbstract)
            {
                var constructor = collectionType.GetDeclaredConstructor(null);
                if (constructor?.IsPublic == true)
                {
                    return collectionType;
                }
            }

            return listOfT;
        }

        return collectionType;
    }

    private MongoTypeMapping? CreateDictionaryTypeMapping(Type dictionaryType)
    {
        var genericArguments = dictionaryType.GenericTypeArguments;
        if (genericArguments[0] != typeof(string))
        {
            return null;
        }

        var elementType = genericArguments[1];
        var elementMappingInfo = new TypeMappingInfo(elementType);
        var elementMapping = FindPrimitiveMapping(elementMappingInfo)
                             ?? FindCollectionMapping(elementMappingInfo);

        var isReadOnly = dictionaryType.GetGenericTypeDefinition() == typeof(ReadOnlyDictionary<,>);

        return elementMapping == null
            ? null
            : new MongoTypeMapping(
                dictionaryType, CreateStringDictionaryComparer(elementMapping, elementType, dictionaryType, isReadOnly));
    }

    private static ValueComparer CreateStringDictionaryComparer(
        CoreTypeMapping elementMapping,
        Type elementType,
        Type dictType,
        bool readOnly = false)
    {
        var unwrappedType = elementType.UnwrapNullableType();

        return (ValueComparer)Activator.CreateInstance(
            elementType == unwrappedType
                ? typeof(StringDictionaryComparer<,>).MakeGenericType(elementType, dictType)
                : typeof(NullableStringDictionaryComparer<,>).MakeGenericType(unwrappedType, dictType),
            elementMapping.Comparer,
            readOnly)!;
    }
}
