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
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

/// <summary>
/// Shared multi-<see cref="MongoQueryMode"/> assertions: "goes native and agrees with the driver-LINQ oracle", and
/// "declines gracefully and the fallback is trustworthy".
/// </summary>
/// <remarks>
/// Owns only the mode orchestration. Each test class supplies a <c>run</c> delegate that creates its own context
/// for a mode and reduces the query to a comparable list.
/// </remarks>
internal static class NativeModeAssert
{
    /// <summary>
    /// Asserts the shape goes native (doesn't throw under <see cref="MongoQueryMode.NativeOnly"/>) and matches the
    /// driver-LINQ oracle exactly. Returns the results for further assertions.
    /// </summary>
    internal static List<T> NativeAndParity<T>(Func<MongoQueryMode, List<T>> run)
    {
        var nativeOnly = run(MongoQueryMode.NativeOnly);
        var driver = run(MongoQueryMode.DriverLinq);

        Assert.Equal(driver, nativeOnly);
        return nativeOnly;
    }

    /// <summary>
    /// Asserts the shape declines gracefully: <see cref="NativeTranslationNotSupportedException"/> under
    /// <see cref="MongoQueryMode.NativeOnly"/>, and <see cref="MongoQueryMode.Native"/> results equal
    /// <see cref="MongoQueryMode.DriverLinq"/>'s.
    /// </summary>
    /// <remarks>
    /// Both halves matter: throwing alone doesn't distinguish a decline from a crash, and doesn't show the fallback
    /// works. Unusable for shapes with no driver-LINQ oracle (SelectMany over reference collections, Intersect/Except,
    /// correlated reducers); those classes keep their own variant.
    /// </remarks>
    internal static List<T> DeclinesCleanly<T>(Func<MongoQueryMode, List<T>> run)
    {
        Assert.Throws<NativeTranslationNotSupportedException>(() => run(MongoQueryMode.NativeOnly));

        var native = run(MongoQueryMode.Native);
        var driver = run(MongoQueryMode.DriverLinq);

        Assert.Equal(driver, native);
        return native;
    }

    /// <summary>
    /// Asserts the shape goes native (<see cref="MongoQueryMode.NativeOnly"/>), that <see cref="MongoQueryMode.Native"/>
    /// agrees, and that both equal a hand-written <paramref name="expected"/>. Returns the NativeOnly results.
    /// </summary>
    /// <param name="run">Creates a context for the mode and reduces the query to a comparable list.</param>
    /// <param name="expected">The correct answer, written by hand (an independent oracle).</param>
    /// <param name="driverKnownWrong">
    /// Set when driver-LINQ returns a wrong answer for this shape (name the bug ID in a comment at the call site).
    /// <see cref="MongoQueryMode.DriverLinq"/> is then asserted to differ from <paramref name="expected"/>, so the
    /// test fails loudly once the driver is fixed and the flag should be dropped. Otherwise DriverLinq must equal it.
    /// </param>
    /// <remarks>
    /// Use instead of <see cref="NativeAndParity{T}"/> when the driver-LINQ oracle can't be trusted.
    /// </remarks>
    internal static List<T> NativeAndExpected<T>(
        Func<MongoQueryMode, List<T>> run,
        List<T> expected,
        bool driverKnownWrong = false)
    {
        var nativeOnly = run(MongoQueryMode.NativeOnly);
        var native = run(MongoQueryMode.Native);
        var driver = run(MongoQueryMode.DriverLinq);

        AssertLabeled("NativeOnly", () => Assert.Equal(expected, nativeOnly));
        AssertLabeled("Native", () => Assert.Equal(expected, native));

        if (driverKnownWrong)
        {
            AssertLabeled("DriverLinq (driverKnownWrong: the driver now returns the expected result; drop the flag)",
                () => Assert.NotEqual(expected, driver));
        }
        else
        {
            AssertLabeled("DriverLinq", () => Assert.Equal(expected, driver));
        }

        return nativeOnly;
    }

    /// <summary>
    /// Runs the same query twice with different parameter values and asserts each result, catching a parameter value
    /// baked into a cached query plan.
    /// </summary>
    /// <param name="runWithParam">
    /// Builds the context (the caller chooses the mode) and runs the query with the given value.
    /// </param>
    /// <remarks>
    /// <paramref name="runWithParam"/> must use ONE query shape: a single lambda capturing the parameter, not a
    /// different lambda or an inlined constant per call. Only then does the second run hit EF's compiled-query
    /// cache, which is the point: a plan that captured the first value would return the first run's rows again.
    /// Both runs must also use the same <see cref="Microsoft.EntityFrameworkCore.DbContext"/> instance (or contexts
    /// sharing one model): the cache key includes the model, and with <c>SingleEntityDbContext</c> /
    /// <c>IgnoreCacheKeyFactory</c> every instance gets its own model, so separate instances never share compiled
    /// queries.
    /// </remarks>
    internal static void TwiceWithDifferentValues<T>(
        Func<object?, List<T>> runWithParam,
        object? first,
        List<T> expectedFirst,
        object? second,
        List<T> expectedSecond)
    {
        var firstResult = runWithParam(first);
        AssertLabeled("first run", () => Assert.Equal(expectedFirst, firstResult));

        var secondResult = runWithParam(second);
        AssertLabeled("second run", () => Assert.Equal(expectedSecond, secondResult));
    }

    private static void AssertLabeled(string label, Action assert)
    {
        try
        {
            assert();
        }
        catch (Xunit.Sdk.XunitException e)
        {
            throw new Xunit.Sdk.XunitException($"{label}: {e.Message}", e);
        }
    }
}
