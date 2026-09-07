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
/// The two standing multi-<see cref="MongoQueryMode"/> assertions the native-translation functional tests are
/// built on: "this shape goes native AND agrees with the driver-LINQ oracle", and "this shape declines
/// gracefully AND the fallback it lands on is itself trustworthy".
/// </summary>
/// <remarks>
/// <para>
/// These own the MODE ORCHESTRATION only — which modes to run, in what order, and what must hold between them.
/// Each test class supplies a <c>run</c> delegate that creates its own context for a given mode and reduces the
/// query to a comparable result list, because the entity type, model customizer and result projection differ
/// per class. That split is deliberate: the part that was copy-pasted verbatim across test classes (and so
/// could drift) is the semantics, not the per-class plumbing.
/// </para>
/// <para>
/// Three test classes previously held byte-identical private copies of both methods, each with its own
/// divergent explanatory comment.
/// </para>
/// </remarks>
internal static class NativeModeAssert
{
    /// <summary>
    /// Asserts the shape goes native — it must NOT throw under <see cref="MongoQueryMode.NativeOnly"/>, which
    /// forbids the fallback — and that the native results match the driver-LINQ oracle exactly. Returns the
    /// results so the caller can additionally assert them against a hand-verified expected value.
    /// </summary>
    internal static List<T> NativeAndParity<T>(Func<MongoQueryMode, List<T>> run)
    {
        var nativeOnly = run(MongoQueryMode.NativeOnly);
        var driver = run(MongoQueryMode.DriverLinq);

        Assert.Equal(driver, nativeOnly);
        return nativeOnly;
    }

    /// <summary>
    /// Asserts the shape is NOT native, gracefully: it throws
    /// <see cref="NativeTranslationNotSupportedException"/> under <see cref="MongoQueryMode.NativeOnly"/> (a
    /// clean decline, not a crash), AND the fallback it relies on delivers results an independent oracle agrees
    /// with (<see cref="MongoQueryMode.Native"/> == <see cref="MongoQueryMode.DriverLinq"/>).
    /// </summary>
    /// <remarks>
    /// Both halves matter. "Every mode throws" alone does not distinguish a graceful decline from a crash, and
    /// "NativeOnly throws" alone does not show the fallback actually works. Where a shape has no driver-LINQ
    /// oracle at all — the <c>SelectMany</c> / reference-collection / <c>Intersect</c> / <c>Except</c> /
    /// correlated-reducer families — this assertion cannot be used; those test classes keep their own
    /// no-oracle variant, with the reason recorded at that call site.
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
