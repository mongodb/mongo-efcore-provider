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
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Helpers for recognizing EF Core query parameters in an expression tree across EF versions.
/// </summary>
internal static class NativeQueryParameter
{
    /// <summary>
    /// Recognizes an EF Core query-parameter node and extracts its name: a prefixed
    /// <see cref="ParameterExpression"/> in EF8/EF9, a <c>QueryParameterExpression</c> in EF10.
    /// </summary>
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

    /// <summary>
    /// Recognizes a constant-index access into a query-parameter array (<c>args[0]</c> in a compiled query whose
    /// lambda takes an array parameter). EF doesn't pre-evaluate this shape, so the element is resolved per
    /// execution — see <see cref="Expressions.MongoParameterExpression.ArrayElementIndex"/>.
    /// <para>
    /// Matches both an <see cref="ExpressionType.ArrayIndex"/> node and the <c>Enumerable.ElementAt</c> call
    /// EF10 rewrites it to.
    /// </para>
    /// </summary>
    public static bool TryGetParameterArrayElementIndex(Expression expr, [NotNullWhen(true)] out string? name, out int index)
    {
        if (expr is BinaryExpression { NodeType: ExpressionType.ArrayIndex } arrayIndex
            && TryGetQueryParameterName(arrayIndex.Left, out name)
            && arrayIndex.Right is ConstantExpression { Value: int constantArrayIndex })
        {
            index = constantArrayIndex;
            return true;
        }

        if (expr is MethodCallExpression { Method.Name: nameof(Enumerable.ElementAt) } call
            && call.Method.IsStatic && call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2
            && TryGetQueryParameterName(call.Arguments[0], out name)
            && call.Arguments[1] is ConstantExpression { Value: int constantElementAtIndex })
        {
            index = constantElementAtIndex;
            return true;
        }

        name = null;
        index = 0;
        return false;
    }
}
