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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

public static class TestDatabaseNamer
{
    // Distinct per assembly and EF version so concurrent test processes sharing a server never collide or clean up each other's databases.
#if EF8
    public const string TestDatabasePrefix = "EF8-FunctionalTest-";
#elif EF9
    public const string TestDatabasePrefix = "EF9-FunctionalTest-";
#elif EF10
    public const string TestDatabasePrefix = "EF10-FunctionalTest-";
#else
    public const string TestDatabasePrefix = "EF-FunctionalTest-";
#endif

    private static readonly string TimeStamp = DateTime.Now.ToString("s").Replace(':', '-');
    private static int DbCount;

    public static string GetUniqueDatabaseName(string? staticName = null)
        => $"{TestDatabasePrefix}{TimeStamp}{staticName}-{Interlocked.Increment(ref DbCount)}";
}
