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
/// <see cref="MongoExpressionTranslator"/> — <c>Regex.IsMatch</c>, both shapes: the forward
/// <c>Regex.IsMatch(field, constantPattern[, options])</c> (<see cref="MongoRegexKind.Pattern"/>) and the reversed
/// <c>Regex.IsMatch(constantInput, fieldPattern)</c> (<see cref="MongoRegexKind.IsMatch"/>, e.g.
/// <c>Regex.IsMatch("Seattle", o.String)</c>).
/// </summary>
/// <remarks>
/// <para>
/// Forward shape: <c>field</c> resolves via <see cref="TryResolveMember"/> to a plain <see langword="string"/>
/// field (not outer-scoped, not computed); <c>pattern</c> is a compile-time constant, a query parameter, or a plain
/// <see langword="string"/> field (not outer-scoped, not computed); the latter two render via <c>$expr</c>/<c>$regexMatch</c>
/// only. Anything else declines. Options must be constant and map via
/// <see cref="MapRegexOptions"/>; an unsupported flag (<see cref="RegexOptions.RightToLeft"/>,
/// <see cref="RegexOptions.ECMAScript"/>, <see cref="RegexOptions.NonBacktracking"/>) declines. The pattern is a
/// live .NET pattern passed to PCRE unchanged — dialect differences are the caller's.
/// </para>
/// <para>
/// Reversed shape: the query-dialect <c>$regularExpression</c> requires a literal pattern, so a field-valued
/// pattern needs the aggregation <c>$regexMatch</c> operator; hence the dedicated, aggregation-only
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

        if (TryTranslateForwardRegexIsMatch(call, out result))
            return true;

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

        if (!TryResolveInnerStringField(Unwrap(call.Arguments[1]), out var fieldNode))
            return false;

        var termNode = new MongoConstantExpression(inputLiteral, forSerialization: null);
        result = new MongoRegexExpression(fieldNode, MongoRegexKind.IsMatch, termNode, negated: false, caseInsensitive);
        return true;
    }

    // Forward shape: Regex.IsMatch(field, constantPattern[, options]) → MongoRegexKind.Pattern.
    private bool TryTranslateForwardRegexIsMatch(MethodCallExpression call, out MongoExpression? result)
    {
        result = null;

        if (!TryResolveInnerStringField(Unwrap(call.Arguments[0]), out var fieldNode))
            return false;

        // The pattern is a constant, a query parameter, or a plain string field (EF-247). Only a constant can use the
        // query dialect ($regularExpression needs a literal); a parameter or field term routes to $expr/$regexMatch
        // (see MongoRegexKind.Pattern). A parameter is never a regex placeholder here: the raw pattern is rendered as
        // a string literal, since the placeholder machinery builds patterns from StartsWith/Contains/... terms.
        var patternArg = Unwrap(call.Arguments[1]);
        MongoExpression patternNode;
        if (patternArg is ConstantExpression { Value: string pattern })
        {
            patternNode = new MongoConstantExpression(pattern, forSerialization: null);
        }
        else if (NativeQueryParameter.TryGetQueryParameterName(patternArg, out var patternParameterName)
                 && patternArg.Type == typeof(string))
        {
            patternNode = new MongoParameterExpression(patternParameterName, forSerialization: null);
        }
        else if (TryResolveInnerStringField(patternArg, out var patternField))
        {
            patternNode = patternField;
        }
        else
        {
            return false;
        }

        var patternOptions = "";
        if (call.Arguments.Count == 3)
        {
            if (Unwrap(call.Arguments[2]) is not ConstantExpression { Value: RegexOptions options })
                return false;

            if (MapRegexOptions(options) is not { } mapped)
                return false;

            patternOptions = mapped;
        }

        result = new MongoRegexExpression(
            fieldNode, MongoRegexKind.Pattern, patternNode, negated: false, patternOptions: patternOptions);
        return true;
    }

    // Ignorable flags don't change matching semantics; mapped flags have a $regexMatch "options" equivalent.
    // RightToLeft/ECMAScript/NonBacktracking change matching semantics with no faithful equivalent, so decline.
    private static string? MapRegexOptions(RegexOptions options)
    {
        const RegexOptions ignorable = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
        const RegexOptions mapped = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline
                                     | RegexOptions.IgnorePatternWhitespace;
        if ((options & ~(ignorable | mapped)) != 0)
            return null;

        return (options.HasFlag(RegexOptions.IgnoreCase) ? "i" : "")
               + (options.HasFlag(RegexOptions.Multiline) ? "m" : "")
               + (options.HasFlag(RegexOptions.Singleline) ? "s" : "")
               + (options.HasFlag(RegexOptions.IgnorePatternWhitespace) ? "x" : "");
    }
}
