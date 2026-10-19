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
}
