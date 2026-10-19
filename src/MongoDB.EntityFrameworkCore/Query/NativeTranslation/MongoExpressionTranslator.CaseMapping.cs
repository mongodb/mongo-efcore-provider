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

using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/>: <c>x.S.ToLower()/ToUpper() == constant</c> (either side, <c>==</c> or
/// <c>!=</c>, including the <c>Invariant</c> forms). <c>$toLower</c>/<c>$toUpper</c> are ASCII-only, so instead: a
/// constant not already in the target case can never be equal (folds to false/true); otherwise an anchored
/// case-insensitive regex.
/// </summary>
/// <remarks>
/// The translate-time check uses the invariant mapping, and the regex match is culture-free, so current-culture
/// special cases (Turkish dotted/dotless I) and case-folding differences between .NET and the server's regex engine
/// are accepted differences.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateCaseMappingComparison(BinaryExpression comparison, out MongoExpression? result)
    {
        result = null;
        if (comparison.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
            return false;

        var (callSide, constantSide) = IsCaseMappingCall(comparison.Left, out _, out _)
            ? (comparison.Left, comparison.Right)
            : (comparison.Right, comparison.Left);

        if (!IsCaseMappingCall(callSide, out var receiver, out var toUpper)
            || Unwrap(constantSide) is not ConstantExpression { Value: string constant }
            || !TryResolveMember(Unwrap(receiver), out var property, out var fieldPath, out var isOuter)
            || isOuter || property.ClrType != typeof(string))
            return false;

        var negated = comparison.NodeType == ExpressionType.NotEqual;
        var mapped = toUpper ? constant.ToUpperInvariant() : constant.ToLowerInvariant();
        if (mapped != constant)
        {
            result = new MongoConstantExpression(negated, forSerialization: null);
            return true;
        }

        result = new MongoRegexExpression(
            new MongoFieldExpression(property, fieldPath), MongoRegexKind.Exact,
            new MongoConstantExpression(constant, property), negated, caseInsensitive: true);
        return true;
    }

    private static bool IsCaseMappingCall(Expression node, out Expression receiver, out bool toUpper)
    {
        receiver = null!;
        toUpper = false;
        if (Unwrap(node) is not MethodCallExpression call || !NativeProjectionBinder.IsCaseMappingCall(call))
            return false;

        toUpper = call.Method.Name is nameof(string.ToUpper) or nameof(string.ToUpperInvariant);
        receiver = call.Object!;
        return true;
    }
}
