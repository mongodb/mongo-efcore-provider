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
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MongoDB.EntityFrameworkCore;

internal static class Check
{
    public static string? NullButNotEmpty(
        string? value,
        [CallerArgumentExpression(nameof(value))] string parameterName = "")
    {
        if (value is not null && value.Length == 0)
        {
            throw new ArgumentException(AbstractionsStrings.ArgumentIsEmpty(parameterName), parameterName);
        }

        return value;
    }

    public static Guid? NotEmpty(Guid? argument, [CallerArgumentExpression(nameof(argument))] string? parameterName = null)
        => argument != Guid.Empty
            ? argument
            : throw new ArgumentException(AbstractionsStrings.ArgumentIsEmpty(parameterName));

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
