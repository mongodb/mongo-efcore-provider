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
/// <see cref="MongoExpressionTranslator"/> —
/// <c>System.Text.RegularExpressions.Regex.IsMatch(input, pattern)</c>, REVERSED-argument shape only: a
/// compile-time-constant <c>input</c> tested against a document-field-valued <c>pattern</c> (e.g.
/// <c>Regex.IsMatch("Seattle", o.String)</c>).
/// </summary>
/// <remarks>
/// The forward shape — <c>Regex.IsMatch(o.String, "^S")</c>, a field-valued input against a constant pattern
/// — is NOT this method's concern: it already succeeds today via the driver-LINQ v3 fallback (there is no
/// dedicated native recognizer for it, and none is added here — see
/// <c>StringTranslationsMongoTest.Regex_IsMatch</c>'s existing baseline). This method exists to add a NATIVE
/// translation for the reversed shape (<c>StringTranslationsMongoTest.Regex_IsMatch_constant_input</c>), which
/// previously fell back to driver-LINQ and failed there too — the driver's own LINQ v3 provider has no
/// translation for it either. It now succeeds natively, rendered via the aggregation-expression
/// <c>$regexMatch</c> operator (see below).
/// <para>
/// <b>Why the reversed shape needs a genuinely different rendering, not just swapped operands.</b> MongoDB's
/// query-dialect <c>$regularExpression</c> BSON type (what the forward shape's fallback, and every other
/// <see cref="MongoRegexKind"/> member, render as) requires a literal pattern — it can never read the pattern
/// from a document field. The reversed shape's pattern IS a document field, so it can only be expressed via
/// the aggregation-expression <c>$regexMatch</c> operator, whose <c>regex</c> operand may itself be an
/// arbitrary expression (including a field reference). This is why the built <see cref="MongoRegexExpression"/>
/// uses <see cref="MongoRegexKind.IsMatch"/> — a dedicated kind, aggregation-dialect only — rather than
/// reusing one of the existing three query-dialect kinds with swapped Field/Term roles.
/// </para>
/// <para>
/// <b>Scope, deliberately narrow — mirrors <see cref="TryTranslateLike"/>'s own discipline:</b>
/// </para>
/// <list type="bullet">
/// <item><c>input</c> (the first argument) must be a compile-time-constant <see langword="string"/>. A
/// field-valued or otherwise computed <c>input</c> is a different, not-yet-supported shape and declines here
/// (falls back to driver-LINQ, which fails the same way it always has).</item>
/// <item><c>pattern</c> (the second argument) must resolve, via <see cref="TryResolveMember"/>, to a plain
/// <see langword="string"/> field — not an outer-scoped reference (out of this plan's scope, same restriction
/// as every other EF-421/EF-322 regex recognizer) and not a computed expression (MongoDB's <c>$regexMatch</c>
/// COULD technically accept a computed <c>regex</c> operand, but this plan does not extend that far).</item>
/// <item>The optional third argument, if present, must be a compile-time-constant <see cref="RegexOptions"/>
/// restricted to <see cref="RegexOptions.None"/> or <see cref="RegexOptions.IgnoreCase"/> — the only two
/// <c>$regexMatch</c> can reproduce without a culture-aware or multiline/singleline semantics mismatch. Any
/// other flag (or combination) declines, exactly mirroring <c>TryMatchRegexMethod</c>'s own
/// <see cref="System.StringComparison"/> restriction for StartsWith/EndsWith/Contains.</item>
/// </list>
/// <para>
/// <b>Malformed field-valued patterns are a genuine, inherent server-error risk</b> — not something this
/// recognizer can or should guard against; see <see cref="MongoRegexKind.IsMatch"/>'s own remarks.
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
