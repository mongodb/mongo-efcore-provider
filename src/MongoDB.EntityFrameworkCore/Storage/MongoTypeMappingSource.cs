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
        var clrType = mappingInfo.ClrType;
        if (clrType == null)
        {
            throw new InvalidOperationException($"Unable to determine CLR type for mappingInfo {mappingInfo}");
        }

        return FindPrimitiveMapping(mappingInfo)
               ?? FindCollectionMapping(mappingInfo)
               ?? base.FindMapping(mappingInfo);
    }

    private static MongoTypeMapping? FindPrimitiveMapping(in TypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType!;
        if (clrType is {IsValueType: true, IsEnum: false} || clrType == typeof(string))
        {
            return new MongoTypeMapping(clrType);
        }

        return null;
    }

    private static MongoTypeMapping? FindCollectionMapping(in TypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType!;
        var itemType = clrType.TryGetItemType();

        if (itemType == null) return null;

        // Support arrays on the entity
        if (clrType.IsArray)
        {
            return CreateCollectionTypeMapping(clrType, itemType);
        }

        // Support generic collections on the entity
        if (clrType is {IsGenericType: true, IsGenericTypeDefinition: false})
        {
            // Regular generic collections and interfaces
            var genericTypeDefinition = clrType.GetGenericTypeDefinition();
            if (supportedCollectionTypes.Contains(genericTypeDefinition) || supportedCollectionInterfaces.Contains(genericTypeDefinition))
            {
                return CreateCollectionTypeMapping(clrType, itemType);
            }

            // Custom generic collections implementing a known interface
            if (genericTypeDefinition.GetInterfaces().Any(i => supportedCollectionInterfaces.Contains(i.GetGenericTypeDefinition())))
            {
                return CreateCollectionTypeMapping(clrType, itemType);
            }
        }

        return null;
    }

    private static MongoTypeMapping? CreateCollectionTypeMapping(Type clrType, Type itemType)
    {
        var elementMappingInfo = new TypeMappingInfo(itemType);
        var elementMapping = FindPrimitiveMapping(elementMappingInfo) ?? FindCollectionMapping(elementMappingInfo);
        return elementMapping == null
            ? null
            : new MongoTypeMapping(clrType, CreateComparer(elementMapping, clrType));
    }

    private static readonly Type[] supportedCollectionTypes =
    [
        typeof(List<>),
        typeof(ReadOnlyCollection<>),
        typeof(Collection<>),
        typeof(ObservableCollection<>)
    ];

    private static readonly Type[] supportedCollectionInterfaces =
    [
        typeof(IList<>),
        typeof(IReadOnlyList<>),
        typeof(IReadOnlyCollection<>),
        typeof(ICollection<>)
    ];

    public static ValueComparer? CreateComparer(CoreTypeMapping elementMapping, Type collectionType)
    {
        var elementType = collectionType.TryGetItemType(typeof(IEnumerable<>))!;

        return (ValueComparer?)Activator.CreateInstance(
            elementType.IsNullableValueType()
                ? typeof(NullableValueTypeListComparer<>).MakeGenericType(Nullable.GetUnderlyingType(elementType) ?? elementType)
            : elementMapping.Comparer.Type.IsAssignableFrom(elementType)
            ? typeof(CollectionComparer<>).MakeGenericType(elementType)
            : typeof(ObjectListComparer<>).MakeGenericType(elementType),
        elementMapping.Comparer.ToNullableComparer(elementType)!);
    }
}
