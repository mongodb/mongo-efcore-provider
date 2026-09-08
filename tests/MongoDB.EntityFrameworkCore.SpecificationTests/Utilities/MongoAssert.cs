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

using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

internal static class MongoAssert
{
    /// <summary>
    /// Assert that the query fails because it involves a correlated subquery
    /// across collections that cannot be translated by the MongoDB provider. Driver-LINQ mode raises this
    /// via a Mongo-specific guard with a message reporting "Unsupported cross-DbSet query"; native-only mode
    /// rejects the same shape earlier, as <see cref="NativeTranslationNotSupportedException"/>. Both signal
    /// the identical unsupported-shape condition, so either is accepted.
    /// </summary>
    public static async Task AssertUnsupportedCrossDbSetQuery(Func<Task> query)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(query);
        if (exception is NativeTranslationNotSupportedException)
        {
            return;
        }

        var message = GetInnermostException(exception).Message;
        Assert.Contains("Unsupported cross-DbSet query", message);
    }

    /// <summary>
    /// Assert that a query throws because the MongoDB provider could not translate it.
    /// Unlike a bare try/catch, this does not treat a result-mismatch assertion thrown by the
    /// query's own caller (e.g. from <c>AssertQuery</c> comparing wrong data) as a translation
    /// failure: only a non-xUnit exception escaping the query counts as "failed to translate".
    /// </summary>
    public static async Task AssertTranslationFailed(Func<Task> query)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(query);
        Assert.False(
            exception is XunitException,
            $"Expected the query to fail to translate, but it instead failed this assertion: {exception}");
    }

    private static Exception GetInnermostException(Exception exception)
    {
        while (exception.InnerException != null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }
}
