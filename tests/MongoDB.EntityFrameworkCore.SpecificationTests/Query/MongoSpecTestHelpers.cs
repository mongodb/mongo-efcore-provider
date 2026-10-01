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
    /// True when <c>MONGODB_EF_QUERY_MODE=NativeOnly</c> (or the alias <c>MONGODB_EF_NATIVE_ONLY=1</c>) flips every
    /// spec context to <c>MongoQueryMode.NativeOnly</c> (see <see cref="Utilities.MongoTestStore.AddProviderOptions"/>).
    /// Translation-failure baselines can differ: the driver-LINQ fallback may log a partial pipeline before failing,
    /// native-only may reject before logging. <c>MONGODB_EF_DIFFERENTIAL=1</c> (set only by the differential runner)
    /// makes this false so default-mode expectations are evaluated under both paths.
    /// </summary>
    internal static bool IsNativeOnly
        => Utilities.SpecQueryMode.IsNativeOnly;

    /// <summary>
    /// Asserts that <paramref name="query"/> fails as a translation failure rather than returning (possibly wrong)
    /// data: <see cref="NativeTranslationNotSupportedException"/> under <c>NativeOnly</c>; an EF
    /// <see cref="InvalidOperationException"/> or driver <see cref="ExpressionNotSupportedException"/> under
    /// <c>Native</c>. <paramref name="additionalAcceptedTypes"/> adds types a suite's fallback genuinely throws (e.g.
    /// <see cref="ArgumentException"/>/<see cref="FormatException"/> for GroupBy). xUnit assertion exceptions are never
    /// accepted, so a wrong-data regression still fails.
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
    /// Asserts that <paramref name="query"/> is rejected as an unsupported cross-<c>DbSet</c> query: an
    /// <see cref="InvalidOperationException"/> ("Unsupported cross-DbSet query between") under driver-LINQ, or
    /// <see cref="NativeTranslationNotSupportedException"/> under native-only.
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
