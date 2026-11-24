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

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

/// <summary>
/// The message of the <see cref="System.InvalidOperationException"/> thrown when reading <c>Nullable&lt;T&gt;.Value</c>
/// over no value. The wording differs by runtime: .NET 10 and earlier, versus .NET 11.
/// </summary>
internal static class NullableValueMessage
{
    private const string Legacy = "Nullable object must have a value";
    private const string Net11 = "Cannot read the Value property of a Nullable object that has no value";

    public static bool Is([NotNullWhen(true)] string? message)
        => message != null && (message.Contains(Legacy) || message.Contains(Net11));

    public static void AssertIs(string? message)
        => Assert.True(Is(message), $"Expected the 'Nullable has no value' message, got: {message}");
}
