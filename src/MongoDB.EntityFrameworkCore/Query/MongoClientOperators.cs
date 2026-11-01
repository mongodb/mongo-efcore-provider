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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Operators the client finishes over the rows of a native query, for a projection whose values the client computes (a case mapping: the server's
/// forms are ASCII-only, so the native projection holds the raw value and the shaper maps it). They cannot be run by the server over the raw value,
/// so they run here, over the shaped rows, with LINQ-to-objects semantics.
/// </summary>
internal static class MongoClientOperators
{
    /// <summary>The rows without repeats, by the result type's default equality (as <c>Enumerable.Distinct</c>).</summary>
    public static ClientDistinctEnumerable<T> Distinct<T>(IEnumerable<T> source)
        => new(source);

    /// <summary>One <see cref="bool"/>: whether the rows hold <paramref name="item"/> (evaluated at enumeration, as the query's parameters are).</summary>
    public static ClientContainsEnumerable<T> Contains<T>(QueryContext queryContext, IEnumerable<T> source, Func<QueryContext, T> item)
        => new(queryContext, source, item);

    /// <summary>The current value of the query parameter named <paramref name="parameterName"/>.</summary>
    public static T ParameterValue<T>(QueryContext queryContext, string parameterName)
#if EF8 || EF9
        => (T)queryContext.ParameterValues[parameterName]!;
#else
        => (T)queryContext.Parameters[parameterName]!;
#endif
}

internal sealed class ClientDistinctEnumerable<T>(IEnumerable<T> source) : IEnumerable<T>, IAsyncEnumerable<T>
{
    public IEnumerator<T> GetEnumerator()
    {
        var seen = new HashSet<T>();
        foreach (var row in source)
        {
            if (seen.Add(row))
            {
                yield return row;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<T>();
        await foreach (var row in ((IAsyncEnumerable<T>)source).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (seen.Add(row))
            {
                yield return row;
            }
        }
    }
}

internal sealed class ClientContainsEnumerable<T>(QueryContext queryContext, IEnumerable<T> source, Func<QueryContext, T> item)
    : IEnumerable<bool>, IAsyncEnumerable<bool>
{
    public IEnumerator<bool> GetEnumerator()
    {
        var wanted = item(queryContext);
        var comparer = EqualityComparer<T>.Default;
        var found = false;
        foreach (var row in source)
        {
            if (comparer.Equals(row, wanted))
            {
                found = true;
                break;
            }
        }

        yield return found;
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public async IAsyncEnumerator<bool> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var wanted = item(queryContext);
        var comparer = EqualityComparer<T>.Default;
        var found = false;
        await foreach (var row in ((IAsyncEnumerable<T>)source).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (comparer.Equals(row, wanted))
            {
                found = true;
                break;
            }
        }

        yield return found;
    }
}
