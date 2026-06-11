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

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

internal static class MongoAssert
{
    /// <summary>
    /// Assert that the query fails because it involves a correlated subquery
    /// across collections that cannot be translated by the MongoDB provider.
    /// </summary>
    public static async Task AssertUnsupportedCrossDbSetQuery(Func<Task> query)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(query);
        var message = GetInnermostException(exception).Message;
        Assert.Contains("Unsupported cross-DbSet query", message);
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
