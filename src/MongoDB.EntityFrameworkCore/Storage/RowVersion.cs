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
using System.Linq;
using System.Numerics;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;

namespace MongoDB.EntityFrameworkCore.Storage;

internal struct RowVersion
{
    public object Current;
    public object Next;

    internal static RowVersion GetFor(IUpdateEntry entry, IProperty property)
        => (RowVersion)GetForInternalMethodInfo.MakeGenericMethod(property.ClrType).Invoke(null, [entry, property])!;

    internal static bool IsARowVersion(IReadOnlyProperty property)
        => property is {IsConcurrencyToken: true, IsNullable: false, ValueGenerated: ValueGenerated.OnAddOrUpdate}
           && SupportedRowVersionTypes.Contains(property.ClrType);

    internal static string DefaultElementName => "_version";

    private static readonly Type[] SupportedRowVersionTypes = [typeof(int), typeof(uint), typeof(long), typeof(ulong)];

    private static RowVersion GetForInternal<T>(IUpdateEntry entry, IProperty property) where T : INumber<T>
    {
        var current = entry.GetCurrentValue<T>(property);
        if (current == T.Zero) current = T.One;
        return new RowVersion {Current = current, Next = current + T.One};
    }

    private static readonly MethodInfo GetForInternalMethodInfo =
        typeof(RowVersion).GetMethod(nameof(GetForInternal), BindingFlags.Static | BindingFlags.NonPublic)!;
}
