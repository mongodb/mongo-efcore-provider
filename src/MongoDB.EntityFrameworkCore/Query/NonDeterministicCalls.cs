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
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Recognizes calls whose value differs per invocation (<c>Random.Next*</c>, <c>Guid.NewGuid</c>). A query pipeline is
/// built once, so evaluating such a call while translating would bake a single value into every row.
/// <c>DateTime.Now</c>/<c>UtcNow</c> are deliberately not included: EF's evaluatable-expression filter leaves them in
/// the tree too, but the native translator declines them and the driver-LINQ bridge, which folds them, runs on every
/// execution, so each execution sees the current time.
/// </summary>
internal static class NonDeterministicCalls
{
    public static bool IsNonDeterministic(MethodCallExpression call)
    {
        var method = call.Method;
        var declaring = method.DeclaringType;
        if (declaring == null)
        {
            return false;
        }

        if (typeof(Random).IsAssignableFrom(declaring))
        {
            return method.Name.StartsWith("Next", StringComparison.Ordinal);
        }

        return declaring == typeof(Guid) && method.IsStatic
            && (method.Name == nameof(Guid.NewGuid) || method.Name == "CreateVersion7");
    }

    public static void ThrowIfNonDeterministic(MethodCallExpression call)
    {
        if (IsNonDeterministic(call))
        {
            throw new InvalidOperationException(
                $"The LINQ expression contains a call to '{call.Method.DeclaringType!.Name}.{call.Method.Name}', which cannot be "
                + "evaluated once per row on the server. Evaluate it before the query and pass the result in as a variable, "
                + "or apply it client-side after AsEnumerable().");
        }
    }
}
