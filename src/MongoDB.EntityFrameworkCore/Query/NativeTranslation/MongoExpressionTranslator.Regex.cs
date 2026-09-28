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
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — the reversed <c>Regex.IsMatch(input, pattern)</c> shape: a constant
/// <c>input</c> tested against a field-valued <c>pattern</c> (e.g. <c>Regex.IsMatch("Seattle", o.String)</c>).
/// The forward shape is handled by the driver-LINQ fallback.
/// </summary>
/// <remarks>
/// <para>
/// The query-dialect <c>$regularExpression</c> requires a literal pattern, so a field-valued pattern needs the
/// aggregation <c>$regexMatch</c> operator; hence the dedicated, aggregation-only
/// <see cref="MongoRegexKind.IsMatch"/> rather than swapping operands on an existing kind.
/// </para>
/// <list type="bullet">
/// <item><c>input</c> must be a constant <see langword="string"/>.</item>
/// <item><c>pattern</c> must resolve via <see cref="TryResolveMember"/> to a plain <see langword="string"/> field
/// (not outer-scoped, not computed).</item>
/// <item>Options, if present, must be a constant <see cref="RegexOptions.None"/> or
/// <see cref="RegexOptions.IgnoreCase"/>, the only ones <c>$regexMatch</c> reproduces faithfully.</item>
/// </list>
/// <para>
/// A malformed field-valued pattern is an inherent server-error risk; see <see cref="MongoRegexKind.IsMatch"/>.
/// </para>
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateRegexIsMatch(MethodCallExpression call, out MongoExpression? result)
    {
        result = null;

        if (!call.Method.IsStatic
            || call.Method.DeclaringType != typeof(Regex)
            || call.Method.Name != nameof(Regex.IsMatch)
            || call.Arguments.Count is not (2 or 3))
        {
            return false;
        }

        if (Unwrap(call.Arguments[0]) is not ConstantExpression { Value: string inputLiteral })
            return false; // input must be a compile-time constant — a field-valued input is a distinct, not-yet-supported shape

        var caseInsensitive = false;
        if (call.Arguments.Count == 3)
        {
            if (Unwrap(call.Arguments[2]) is not ConstantExpression { Value: RegexOptions options })
                return false;

            if (options is not (RegexOptions.None or RegexOptions.IgnoreCase))
                return false; // culture/multiline/singleline etc. have no faithful $regexMatch equivalent here

            caseInsensitive = options == RegexOptions.IgnoreCase;
        }

        if (!TryResolveMember(Unwrap(call.Arguments[1]), out var patternProperty, out var patternFieldPath, out var isOuter)
            || isOuter
            || patternProperty.ClrType != typeof(string))
        {
            return false;
        }

        var fieldNode = new MongoFieldExpression(patternProperty, patternFieldPath);
        var termNode = new MongoConstantExpression(inputLiteral, forSerialization: null);
        result = new MongoRegexExpression(fieldNode, MongoRegexKind.IsMatch, termNode, negated: false, caseInsensitive);
        return true;
    }
}
