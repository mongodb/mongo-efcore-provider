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
}
