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

internal sealed class CollectionComparer<T> : ValueComparer<IList<T>>
{
    public CollectionComparer(CoreTypeMapping elementMapping)
        : base(
            (a, b) => Compare(a, b, (ValueComparer<T>)elementMapping.Comparer),
            o => GetHashCode(o, (ValueComparer<T>)elementMapping.Comparer),
            source => Snapshot(source, (ValueComparer<T>)elementMapping.Comparer))
    {
    }

    public override Type Type => typeof(IList<T>);

    private static bool Compare(IList<T>? a, IList<T>? b, ValueComparer<T> elementComparer)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null)
        {
            return b is null;
        }

        if (b is null || a.Count != b.Count)
        {
            return false;
        }

        // Note: the following currently boxes every element access because ValueComparer isn't really
        // generic (see https://github.com/aspnet/EntityFrameworkCore/issues/11072)
        for (var i = 0; i < a.Count; i++)
        {
            if (!elementComparer.Equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetHashCode(IList<T> source, ValueComparer<T> elementComparer)
    {
        var hash = new HashCode();

        foreach (var el in source)
        {
            hash.Add(el, elementComparer);
        }

        return hash.ToHashCode();
    }

    private static IList<T> Snapshot(IList<T> source, ValueComparer<T> elementComparer)
    {
        if (source.GetType().IsArray)
        {
            var snapshot = new T[source.Count];

            for (var i = 0; i < source.Count; i++)
            {
                snapshot[i] = elementComparer.Snapshot(source[i]);
            }

            return snapshot;
        }
        else
        {
            var snapshot = source is List<T>
                ? new List<T>(source.Count)
                : (IList<T>)Activator.CreateInstance(source.GetType())!;

            foreach (var e in source)
            {
                snapshot.Add(elementComparer.Snapshot(e));
            }

            return snapshot;
        }
    }
}
