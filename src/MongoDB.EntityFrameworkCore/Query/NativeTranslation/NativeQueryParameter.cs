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

    /// <summary>
    /// Recognizes an <c>args[i]</c>-shaped access into a query-parameter ARRAY, whose left/receiver operand is
    /// itself an EF query parameter (<see cref="TryGetQueryParameterName"/>) and whose index operand is a
    /// constant <see langword="int"/>. This arises from a compiled query whose lambda takes an array parameter
    /// directly (e.g. <c>EF.CompileQuery((Ctx ctx, string[] args) =&gt; ctx.Set.Where(c =&gt; c.Id ==
    /// args[0]))</c>) — the index cannot be resolved until the array's runtime value is known, so unlike an
    /// ordinary closure-captured array index (which EF's own parameter extraction pre-evaluates to a single
    /// constant/parameter before the provider ever sees it), this shape reaches the native translator as a
    /// genuine two-part expression tree that must be resolved at Build (per-execution) time — see
    /// <see cref="Expressions.MongoParameterExpression.ArrayElementIndex"/>.
    /// <para>
    /// Matches TWO tree shapes, both observed to arise from the identical C# <c>args[0]</c> source depending on
    /// how far EF Core's own preprocessing rewrites the indexer before the provider's translator ever sees it:
    /// a plain <see cref="ExpressionType.ArrayIndex"/> node, and (MEASURED — the shape EF10 actually produces
    /// for this ticket's motivating case) a rewritten <c>Enumerable.ElementAt&lt;T&gt;(source, index)</c> call.
    /// </para>
    /// </summary>
    /// <param name="expr">The candidate expression.</param>
    /// <param name="name">The query-parameter name carrying the array, when recognized.</param>
    /// <param name="index">The constant element index, when recognized.</param>
    /// <returns><see langword="true"/> if <paramref name="expr"/> is a query-parameter array index access.</returns>
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
