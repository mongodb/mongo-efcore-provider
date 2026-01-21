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
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query.Internal;
using MongoDB.Driver.Linq;

namespace MongoDB.EntityFrameworkCore;

internal static class Check
{
    public static string? NotEmptyButCanBeNull(
        string? value,
        [CallerArgumentExpression(nameof(value))] string parameterName = "")
    {
        if (value is not null && value.Length == 0)
        {
            throw new ArgumentException($"The string argument '{nameof(parameterName)}' cannot be empty.", parameterName);
        }

        return value;
    }

    public static void IsEfQueryProvider(
        IQueryable source,
        [CallerMemberName] string? memberName = null)
    {
        if (source.Provider is EntityQueryProvider)
        {
            return;
        }

        if (source.Provider is IMongoQueryProvider)
        {
            throw new ArgumentException($"The method '{memberName}' can only be called on an IQueryable that starts as a DbSet in EF Core. The IQueryable used came directly from the MongoDB driver and so cannot be used with EF Core-specific extensions.");
        }

        throw new ArgumentException($"The method '{memberName}' can only be called on an IQueryable that starts as a DbSet in EF Core. The IQueryable came from a non-MongoDB LINQ implementation.");
    }

    public static Guid? NotEmpty(Guid? argument, [CallerArgumentExpression(nameof(argument))] string? parameterName = null)
        => argument != Guid.Empty
            ? argument
            : throw new ArgumentException($"The string argument '{nameof(parameterName)}' cannot be empty.", parameterName);

    public static T? IsDefinedOrNull<T>(T? argument,
        [CallerArgumentExpression(nameof(argument))]
        string? parameterName = null) where T : struct
        => argument == null
            ? null
            : Enum.IsDefined(typeof(T), argument)
                ? argument
                : throw new ArgumentOutOfRangeException(parameterName);

    public static int? InRange(int? argument, int min, int max,
        [CallerArgumentExpression(nameof(argument))] string? parameterName = null)
        => argument >= min && argument <= max
            ? argument
            : throw new ArgumentOutOfRangeException(parameterName, argument, "Value must be between {min} and {max} inclusive.");
}
