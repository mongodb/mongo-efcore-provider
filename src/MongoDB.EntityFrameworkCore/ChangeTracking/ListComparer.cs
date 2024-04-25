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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.ChangeTracking;

internal sealed class ListComparer<TElement>(ValueComparer<TElement> elementComparer)
    : ValueComparer<IEnumerable<TElement>>(
        (a, b) => Compare(a, b, elementComparer),
        o => GetHashCode(o, elementComparer),
        source => Snapshot(source, elementComparer))
{
    private static bool Compare(IEnumerable<TElement>? a, IEnumerable<TElement>? b, ValueComparer<TElement> elementComparer)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null) return b is null;
        if (b is null) return false;

        if (a is IList<TElement> aList && b is IList<TElement> bList)
        {
            if (aList.Count != bList.Count) return false;

            for (var i = 0; i < aList.Count; i++)
            {
                var (el1, el2) = (aList[i], bList[i]);
                if (el1 is null)
                {
                    if (el2 is null) continue;
                    return false;
                }

                if (el2 is null) return false;
                if (!elementComparer.Equals(el1, el2)) return false;
            }

            return true;
        }

        throw new InvalidOperationException(
            CoreStrings.BadListType(
                (a is IList<TElement?> ? b : a).GetType().ShortDisplayName(),
                typeof(IList<>).MakeGenericType(elementComparer.Type).ShortDisplayName()));
    }

    private static int GetHashCode(IEnumerable<TElement> source, ValueComparer<TElement> elementComparer)
    {
        var hash = new HashCode();
        foreach (var element in source)
        {
            hash.Add(element, elementComparer);
        }

        return hash.ToHashCode();
    }

    private static IEnumerable<TElement> Snapshot(IEnumerable<TElement> source, ValueComparer<TElement> elementComparer)
    {
        // Common array case first
        if (source is TElement[] sourceArray)
        {
            var snapshot = new TElement[sourceArray.Length];
            for (var i = 0; i < sourceArray.Length; i++)
                snapshot[i] = elementComparer.Snapshot(sourceArray[i]);
            return snapshot;
        }

        var sourceType = source.GetType();

        // Common List (not subtypes)
        if (sourceType == typeof(List<TElement>))
        {
            return ((List<TElement>)source).ConvertAll(elementComparer.Snapshot);
        }

        var constructors = sourceType.GetConstructors().ToArray();

        // Handle anything that has a constructor that accepts an IList<TElement>
        if (HasConstructorWithSingleParameterOf<IList<TElement>>(constructors))
        {
            return CreateInstance(sourceType, source.Select(elementComparer.Snapshot).ToList());
        }

        // Handle anything that has a constructor that accepts an IEnumerable
        if (HasConstructorWithSingleParameterOf<IEnumerable>(constructors))
        {
            return CreateInstance(sourceType, source.Select(elementComparer.Snapshot));
        }

        // Out of options, inform developer what they can do about it
        throw new NotSupportedException(
            $"Collection type '{sourceType.ShortDisplayName()
            }' is unusable by change tracking. Consider adding a constructor that accepts an 'IEnumerable<{
                typeof(TElement).ShortDisplayName()}>' to it.");
    }

    private static IEnumerable<TElement> CreateInstance(Type type, IEnumerable<TElement> parameter)
        => (IEnumerable<TElement>)Activator.CreateInstance(type, parameter)!;

    private static bool HasConstructorWithSingleParameterOf<T>(IEnumerable<ConstructorInfo> constructors)
        => constructors.Any(c => c.GetParameters().Length == 1 && typeof(T).IsAssignableFrom(c.GetParameters()[0].ParameterType));
}
