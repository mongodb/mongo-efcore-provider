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
using System.Threading.Tasks;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

/// <summary>
/// Shared assertion helpers for the Northwind specification-test overrides.
/// </summary>
internal static class MongoSpecTestHelpers
{
    /// <summary>
    /// True when the current run has <c>MONGODB_EF_NATIVE_ONLY=1</c> set, flipping every spec context to
    /// <c>MongoQueryMode.NativeOnly</c> (see <see cref="Utilities.MongoTestStore.AddProviderOptions"/>). Some
    /// translation-failure baselines depend on how far the query got before rejecting it: the driver-LINQ
    /// fallback path can log a partial pipeline before failing, while the native-only path may reject the
    /// query before anything is logged. Tests with such a baseline should branch on this flag.
    /// </summary>
    internal static bool IsNativeOnly
        => Environment.GetEnvironmentVariable("MONGODB_EF_NATIVE_ONLY") == "1";

    /// <summary>
    /// Asserts that <paramref name="query"/> fails as a <em>translation</em> failure rather than executing
    /// and returning (potentially wrong) data. A shape the native translator does not support must fail
    /// with one of the accepted translation-failure exception types; the exact type depends on the query
    /// mode and how far the driver-LINQ fallback gets:
    /// <list type="bullet">
    /// <item><see cref="NativeTranslationNotSupportedException"/> under <c>MongoQueryMode.NativeOnly</c>;</item>
    /// <item>an EF <see cref="InvalidOperationException"/> (CoreStrings.TranslationFailed or an internal
    /// guard) or a driver <see cref="ExpressionNotSupportedException"/> under the default <c>Native</c> mode.</item>
    /// </list>
    /// Callers may pass <paramref name="additionalAcceptedTypes"/> for extra exception types that a
    /// particular suite's driver-LINQ fallback genuinely throws (e.g. <see cref="ArgumentException"/> /
    /// <see cref="FormatException"/> for GroupBy shapes). Data-assertion failures (xUnit assertion
    /// exceptions) are deliberately NOT accepted, so a future wrong-data regression still turns the test
    /// red rather than being masked.
    /// </summary>
    internal static async Task AssertNativeTranslationFailedAsync(
        Func<Task> query, params Type[] additionalAcceptedTypes)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(query);
        if (exception is NativeTranslationNotSupportedException
            or InvalidOperationException
            or ExpressionNotSupportedException)
        {
            return;
        }

        foreach (var acceptedType in additionalAcceptedTypes)
        {
            if (acceptedType.IsInstanceOfType(exception))
            {
                return;
            }
        }

        throw new XunitException(
            $"Expected a translation failure but the query threw {exception.GetType()}: {exception.Message}");
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="AssertNativeTranslationFailedAsync"/>, for the handful of
    /// non-async Northwind test overrides.
    /// </summary>
    internal static void AssertNativeTranslationFailed(Action query, params Type[] additionalAcceptedTypes)
    {
        var exception = Assert.ThrowsAny<Exception>(query);
        if (exception is NativeTranslationNotSupportedException
            or InvalidOperationException
            or ExpressionNotSupportedException)
        {
            return;
        }

        foreach (var acceptedType in additionalAcceptedTypes)
        {
            if (acceptedType.IsInstanceOfType(exception))
            {
                return;
            }
        }

        throw new XunitException(
            $"Expected a translation failure but the query threw {exception.GetType()}: {exception.Message}");
    }

    /// <summary>
    /// Asserts that <paramref name="query"/> is rejected as an unsupported cross-<c>DbSet</c> (multi-collection)
    /// query. Driver-LINQ mode raises this as an <see cref="InvalidOperationException"/> from a Mongo-specific
    /// guard, with a message reporting "Unsupported cross-DbSet query between"; native-only mode rejects the
    /// same shape earlier, as <see cref="NativeTranslationNotSupportedException"/>. Both signal the identical
    /// unsupported-shape condition, so either is accepted.
    /// </summary>
    internal static async Task AssertNoMultiCollectionQuerySupportAsync(Func<Task> query)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(query);
        if (exception is NativeTranslationNotSupportedException)
        {
            return;
        }

        Assert.Contains("Unsupported cross-DbSet query between", Assert.IsType<InvalidOperationException>(exception).Message);
    }
}
