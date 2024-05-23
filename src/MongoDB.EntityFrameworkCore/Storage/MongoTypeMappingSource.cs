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
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.ChangeTracking;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Create a <see cref="MongoTypeMapping"/> (or <see cref="CoreTypeMapping"/>) for
/// each property of an entity that should be mapped to the underlying MongoDB database.
/// </summary>
public class MongoTypeMappingSource(TypeMappingSourceDependencies dependencies)
    : TypeMappingSource(dependencies)
{
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
        if (clrType is {IsValueType: true} || clrType == typeof(string))
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
            var genericTypeDefinition = clrType.GetGenericTypeDefinition();

            if (IsTypeOrHasInterface(genericTypeDefinition, SupportedCollectionTypes, SupportedCollectionInterfaces))
            {
                return CreateCollectionTypeMapping(clrType, elementType);
            }
        }

        return null;
    }

    private static bool IsTypeOrHasInterface(Type genericTypeDefinition, Type[] supportedTypes, Type[] supportedInterfaces)
        => supportedTypes.Contains(genericTypeDefinition)
           || supportedInterfaces.Contains(genericTypeDefinition)
           || genericTypeDefinition.GetInterfaces().Any(i => i.IsGenericType && supportedInterfaces.Contains(i.GetGenericTypeDefinition()));

    private static readonly Type[] SupportedCollectionTypes =
    [
        typeof(List<>),
        typeof(ReadOnlyCollection<>),
        typeof(Collection<>),
        typeof(ObservableCollection<>)
    ];

    private static readonly Type[] SupportedCollectionInterfaces =
    [
        typeof(IList<>),
        typeof(IReadOnlyList<>),
        typeof(IEnumerable<>)
    ];

    private MongoTypeMapping? CreateCollectionTypeMapping(Type clrType, Type elementType)
    {
        var elementMappingInfo = new TypeMappingInfo(elementType);
        var elementMapping = FindPrimitiveMapping(elementMappingInfo) ?? FindCollectionMapping(elementMappingInfo);
        return elementMapping == null
            ? null
            : new MongoTypeMapping(clrType, CreateCollectionComparer(elementMapping, clrType, elementType));
    }

    private static ValueComparer? CreateCollectionComparer(CoreTypeMapping elementMapping, Type collectionType, Type elementType)
    {
        var modelClrType = collectionType;
        var typeToInstantiate = FindTypeToInstantiate();

        return (ValueComparer?)Activator.CreateInstance(
            elementType.IsNullableValueType()
                ? typeof(ListOfNullableValueTypesComparer<,>).MakeGenericType(typeToInstantiate,
                    elementType.UnwrapNullableType())
                : elementType.IsValueType
                    ? typeof(ListOfValueTypesComparer<,>).MakeGenericType(typeToInstantiate, elementType)
                    : typeof(ListOfReferenceTypesComparer<,>).MakeGenericType(typeToInstantiate, elementType),
            elementMapping.Comparer.ToNullableComparer(elementType)!);

        Type FindTypeToInstantiate()
        {
            if (modelClrType.IsArray)
            {
                return modelClrType;
            }

            var listOfT = typeof(List<>).MakeGenericType(elementType);

            if (modelClrType.IsAssignableFrom(listOfT))
            {
                if (!modelClrType.IsAbstract)
                {
                    var constructor = modelClrType.GetDeclaredConstructor(null);
                    if (constructor?.IsPublic == true)
                    {
                        return modelClrType;
                    }
                }

                return listOfT;
            }

            return modelClrType;
        }
    }
}
