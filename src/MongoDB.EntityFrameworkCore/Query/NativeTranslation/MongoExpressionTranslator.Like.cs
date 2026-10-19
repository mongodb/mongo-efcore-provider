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
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>EF.Functions.Like(matchExpression, pattern)</c>.
/// </summary>
/// <remarks>
/// Driver LINQ has no <c>Like</c> translation, so case-sensitivity is this provider's own choice (see
/// <c>MongoQueryLanguageRenderer.RenderRegex</c>). Only a constant <c>pattern</c> is handled:
/// <list type="bullet">
/// <item>Constant <c>matchExpression</c>: folded to a constant bool in C# via the same converted pattern. This
/// must be handled here, because <c>DbFunctionsExtensions.Like</c>'s C# body always throws, so client
/// evaluation can never succeed.</item>
/// <item>Plain <see langword="string"/> field: a <see cref="MongoRegexExpression"/> with
/// <see cref="MongoRegexKind.Like"/>.</item>
/// </list>
/// Declined: the escape-character overload, a non-constant pattern (per-document patterns can't be converted at
/// translation time), and non-string receivers.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateLike(MethodCallExpression call, out MongoExpression? result)
    {
        result = null;

        if (call.Method.DeclaringType != typeof(DbFunctionsExtensions)
            || call.Method.Name != nameof(DbFunctionsExtensions.Like)
            || call.Arguments.Count != 3)
        {
            // (DbFunctions, matchExpression, pattern); the 4-argument escape-character overload declines.
            return false;
        }

        if (Unwrap(call.Arguments[2]) is not ConstantExpression { Value: string patternLiteral })
            return false;

        var matchExpr = Unwrap(call.Arguments[1]);

        if (matchExpr is ConstantExpression { Value: string matchLiteral })
        {
            var regexPattern = MongoRegexPatternBuilder.BuildPattern(patternLiteral, MongoRegexKind.Like);
            var matches = Regex.IsMatch(matchLiteral, regexPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            result = new MongoConstantExpression(matches, forSerialization: null);
            return true;
        }

        if (!TryResolveInnerStringField(matchExpr, out var fieldNode))
            return false;

        var termNode = new MongoConstantExpression(patternLiteral, forSerialization: null);
        result = new MongoRegexExpression(fieldNode, MongoRegexKind.Like, termNode, negated: false);
        return true;
    }
}
