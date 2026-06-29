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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Helpers for recognizing EF Core query parameters in an expression tree across EF versions.
/// </summary>
internal static class NativeQueryParameter
{
    /// <summary>
    /// Recognizes an EF Core query-parameter node and extracts its name. In EF8/EF9 a query parameter is a
    /// <see cref="ParameterExpression"/> whose name carries <c>QueryCompilationContext.QueryParameterPrefix</c>;
    /// in EF10 it is a typed <c>QueryParameterExpression</c>. The version difference is encapsulated here so
    /// the native translator's call sites stay version-agnostic.
    /// </summary>
    /// <param name="expr">The candidate expression.</param>
    /// <param name="name">The query-parameter name when <paramref name="expr"/> is a query parameter.</param>
    /// <returns><see langword="true"/> if <paramref name="expr"/> is an EF query parameter.</returns>
    public static bool TryGetQueryParameterName(Expression expr, [NotNullWhen(true)] out string? name)
    {
#if EF8 || EF9
        if (expr is ParameterExpression param
            && param.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal) == true)
        {
            name = param.Name;
            return true;
        }
#else
        if (expr is QueryParameterExpression queryParam)
        {
            name = queryParam.Name;
            return true;
        }
#endif

        name = null;
        return false;
    }
}
