// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Originally from EFCore NonCapturingLazyInitializer.cs

using System.Threading;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

internal static class NonCapturingLazyInitializer
{
    public static TValue EnsureInitialized<TParam, TValue>(
        ref TValue? target,
        TParam param,
        Func<TParam, TValue> valueFactory)
        where TValue : class
    {
        var tmp = Volatile.Read(ref target);
        if (tmp != null)
        {
            return tmp;
        }

        Interlocked.CompareExchange(ref target, valueFactory(param), null);

        return target;
    }
}
