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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

/// <summary>
/// The query provider the functional and specification suites run under, resolved from the environment. This is the
/// only resolver: the specification project references this assembly and uses this type directly.
/// <list type="bullet">
/// <item><c>MONGODB_EF_QUERY_PROVIDER=NativeOnly|DriverLinq|Native</c> (case-insensitive) selects the mode. Any other
/// non-empty value, including a numeric string, throws rather than silently running as <c>Native</c>.</item>
/// <item><c>MONGODB_EF_NATIVE_ONLY=1</c> is an alias for <c>NativeOnly</c>. Combining it with a
/// <c>MONGODB_EF_QUERY_PROVIDER</c> other than <c>NativeOnly</c> throws, so an exported alias can never silently
/// override the mode the differential runner asked for.</item>
/// <item><c>MONGODB_EF_DIFFERENTIAL=1</c> is set only by the differential runner: <see cref="IsNativeOnly"/> is then
/// false, and <see cref="AssertRefusal{TDefault}"/> accepts either refusal type, so default-mode expectations are
/// evaluated under both paths.</item>
/// </list>
/// A resolution failure throws from the module initializer, so the test process fails to start instead of running
/// the wrong mode.
/// </summary>
public static class TestQueryProvider
{
    internal const string QueryProviderVariable = "MONGODB_EF_QUERY_PROVIDER";
    internal const string NativeOnlyAliasVariable = "MONGODB_EF_NATIVE_ONLY";
    internal const string DifferentialVariable = "MONGODB_EF_DIFFERENTIAL";

    private static int _initialized;

    public static MongoQueryProvider Current { get; } = Resolve();

    public static bool IsDifferential
        => Environment.GetEnvironmentVariable(DifferentialVariable) == "1";

    public static bool IsNativeOnly => !IsDifferential && Current == MongoQueryProvider.NativeOnly;
    public static bool IsDriverLinq => Current == MongoQueryProvider.DriverLinq;

    /// <summary>
    /// Sets <c>MongoOptionsExtension.DefaultQueryProvider</c> to <see cref="Current"/> and, once per process, warns on
    /// stderr when <c>MONGODB_EF_DIFFERENTIAL=1</c> is set. Called from the module initializer of both the functional
    /// and the specification assemblies; both set the same value from this resolver, so the order does not matter.
    /// </summary>
    public static void ApplyProcessDefault()
    {
        Microsoft.EntityFrameworkCore.MongoOptionsExtension.DefaultQueryProvider = Current;

        if (Interlocked.Exchange(ref _initialized, 1) == 0 && IsDifferential)
        {
            Console.Error.WriteLine(
                $"WARNING: {DifferentialVariable}=1 is set (query provider {Current}). NativeOnly-specific expectations are "
                + "relaxed to accept either refusal type; this is intended only for tests/tools/native-parity-diff.sh.");
        }
    }

    public static MongoQueryProvider Resolve()
        => Resolve(
            Environment.GetEnvironmentVariable(NativeOnlyAliasVariable),
            Environment.GetEnvironmentVariable(QueryProviderVariable));

    /// <summary>Pure form of <see cref="Resolve()"/> over the raw environment values, for testing.</summary>
    internal static MongoQueryProvider Resolve(string? nativeOnlyAlias, string? queryProvider)
    {
        var alias = string.IsNullOrEmpty(nativeOnlyAlias)
            ? false
            : nativeOnlyAlias switch
            {
                "1" => true,
                "0" => false,
                _ => throw new InvalidOperationException(
                    $"{NativeOnlyAliasVariable}='{nativeOnlyAlias}' is not recognised; use 1 (or unset it).")
            };

        MongoQueryProvider? requested = null;
        if (!string.IsNullOrWhiteSpace(queryProvider))
        {
            var trimmed = queryProvider.Trim();
            // Enum.TryParse also accepts numeric strings ("7" parses to an undefined value) and comma-separated
            // flag lists; only a single defined member name is accepted.
            if (!char.IsLetter(trimmed[0])
                || trimmed.Contains(',')
                || !Enum.TryParse<MongoQueryProvider>(trimmed, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                throw new InvalidOperationException(
                    $"{QueryProviderVariable}='{queryProvider}' is not recognised; expected one of "
                    + $"{string.Join(", ", Enum.GetNames<MongoQueryProvider>())} (or unset it for Native).");
            }

            requested = parsed;
        }

        if (alias)
        {
            if (requested is not null and not MongoQueryProvider.NativeOnly)
            {
                throw new InvalidOperationException(
                    $"{NativeOnlyAliasVariable}=1 (alias for NativeOnly) conflicts with {QueryProviderVariable}={requested}. "
                    + $"Unset {NativeOnlyAliasVariable} or set {QueryProviderVariable}=NativeOnly.");
            }

            return MongoQueryProvider.NativeOnly;
        }

        return requested ?? MongoQueryProvider.Native;
    }

    public static void AssertRefusal<TDefault>(Action query) where TDefault : Exception
    {
        if (IsDifferential)
        {
            AssertEitherRefusal<TDefault>(Assert.ThrowsAny<Exception>(query));
        }
        else if (IsNativeOnly)
        {
            Assert.Throws<NativeTranslationNotSupportedException>(query);
        }
        else
        {
            Assert.Throws<TDefault>(query);
        }
    }

    public static async Task AssertRefusalAsync<TDefault>(Func<Task> query) where TDefault : Exception
    {
        if (IsDifferential)
        {
            AssertEitherRefusal<TDefault>(await Assert.ThrowsAnyAsync<Exception>(query));
        }
        else if (IsNativeOnly)
        {
            await Assert.ThrowsAsync<NativeTranslationNotSupportedException>(query);
        }
        else
        {
            await Assert.ThrowsAsync<TDefault>(query);
        }
    }

    private static void AssertEitherRefusal<TDefault>(Exception exception) where TDefault : Exception
        => Assert.True(
            exception is NativeTranslationNotSupportedException or TDefault,
            $"Expected {nameof(NativeTranslationNotSupportedException)} or {typeof(TDefault).Name} but got {exception.GetType().Name}: {exception.Message}");
}
