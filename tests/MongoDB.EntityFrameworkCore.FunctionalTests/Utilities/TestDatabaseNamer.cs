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
    // Distinct per assembly, EF version and (when known) server version so concurrent test processes sharing a server
    // never collide or clean up each other's databases, while stale databases from earlier runs are still matched.
#if EF8
    private const string EfPrefix = "EF8-Func-";
#elif EF9
    private const string EfPrefix = "EF9-Func-";
#elif EF10
    private const string EfPrefix = "EF10-Func-";
#else
    private const string EfPrefix = "EF-Func-";
#endif

    public static readonly string TestDatabasePrefix = EfPrefix + ServerVersionSegment();

    private static string ServerVersionSegment()
    {
        // Database names cannot contain '.', so "8.0" becomes "8_0". Omitted when the server version is not declared.
        var version = Environment.GetEnvironmentVariable("MONGODB_VERSION");
        return string.IsNullOrWhiteSpace(version) ? "" : "v" + version.Trim().Replace('.', '_') + "-";
    }

    private static readonly string TimeStamp = DateTime.Now.ToString("s").Replace(':', '-');
    private static int DbCount;

    // MongoDB limits database names to 63 characters, so the static part is truncated to fit; the counter keeps names unique.
    private const int MaxDatabaseNameLength = 63;

    public static string GetUniqueDatabaseName(string? staticName = null)
    {
        var suffix = $"-{Interlocked.Increment(ref DbCount)}";
        var available = MaxDatabaseNameLength - TestDatabasePrefix.Length - TimeStamp.Length - suffix.Length;
        staticName = staticName is { Length: > 0 } && available > 0 ? staticName[..Math.Min(staticName.Length, available)] : "";
        return $"{TestDatabasePrefix}{TimeStamp}{staticName}{suffix}";
    }
}
