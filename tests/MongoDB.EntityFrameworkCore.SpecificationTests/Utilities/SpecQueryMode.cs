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

using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

/// <summary>
/// Resolves the query mode the specification suite runs under from the environment (the test projects do not
/// reference each other, so this duplicates the functional-suite <c>TestQueryMode.Resolve</c>).
/// <list type="bullet">
/// <item><c>MONGODB_EF_QUERY_MODE=NativeOnly|DriverLinq|Native</c> selects the mode.</item>
/// <item><c>MONGODB_EF_NATIVE_ONLY=1</c> is an alias for <c>NativeOnly</c>.</item>
/// <item><c>MONGODB_EF_DIFFERENTIAL=1</c> is set only by the differential runner: <see cref="IsNativeOnly"/> is then
/// false so default-mode expectations are evaluated under both paths.</item>
/// </list>
/// </summary>
internal static class SpecQueryMode
{
    internal static MongoQueryMode Current { get; } = Resolve();

    internal static bool IsDifferential
        => Environment.GetEnvironmentVariable("MONGODB_EF_DIFFERENTIAL") == "1";

    internal static bool IsNativeOnly
        => !IsDifferential && Current == MongoQueryMode.NativeOnly;

    internal static MongoQueryMode Resolve()
        => Environment.GetEnvironmentVariable("MONGODB_EF_NATIVE_ONLY") == "1"
            ? MongoQueryMode.NativeOnly
            : Enum.TryParse<MongoQueryMode>(Environment.GetEnvironmentVariable("MONGODB_EF_QUERY_MODE"), out var m)
                ? m
                : MongoQueryMode.Native;
}
