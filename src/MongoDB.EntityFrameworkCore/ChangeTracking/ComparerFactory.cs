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
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.ChangeTracking;

internal static class ComparerFactory
{
    public static ValueComparer? CreateComparer(CoreTypeMapping elementMapping, Type collectionType)
    {
        // Only single dimension arrays are supported
        if (collectionType.IsArray && collectionType.GetArrayRank() != 1)
        {
            return null;
        }

        var itemType = collectionType.TryGetItemType(typeof(IEnumerable<>))!;
        var nullableType = Nullable.GetUnderlyingType(itemType);

        var comparerType = nullableType != null
            ? typeof(NullableCollectionComparer<>).MakeGenericType(nullableType)
            : typeof(CollectionComparer<>).MakeGenericType(itemType);

        return (ValueComparer)Activator.CreateInstance(comparerType, elementMapping)!;
    }
}
