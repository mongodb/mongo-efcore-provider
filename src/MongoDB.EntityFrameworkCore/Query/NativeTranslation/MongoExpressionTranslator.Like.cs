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
/// Before this, EVERY <c>EF.Functions.Like</c> call failed outright, in both native and driver-LINQ mode — the
/// C# driver's own LINQ v3 provider has no <c>Like</c> translation either (confirmed empirically: it throws
/// <c>ExpressionNotSupportedException</c>), so there is no existing driver-LINQ behavior to preserve parity
/// with here. This is new capability, not a native conversion of previously-fallback-only behavior — which
/// means its case-sensitivity is this provider's own choice (see <c>MongoQueryLanguageRenderer.RenderRegex</c>'s
/// remarks) rather than something dictated by matching a prior baseline.
/// <para>
/// <b>Scope, deliberately narrow.</b> Only a compile-time-constant <c>pattern</c> against either a plain
/// <see langword="string"/>-typed root field or another compile-time constant is handled:
/// </para>
/// <list type="bullet">
/// <item>Both <c>matchExpression</c> and <c>pattern</c> constant (e.g. <c>Like_all_literals</c>) — the whole
/// call collapses to a compile-time constant <see langword="true"/>/<see langword="false"/>, evaluated here in
/// C# via <see cref="Regex.IsMatch(string,string,RegexOptions)"/> against the same converted pattern
/// <see cref="MongoRegexPatternBuilder.BuildPattern"/> would build. This shape MUST be handled, not merely
/// may be: <c>DbFunctionsExtensions.Like</c>'s C# body unconditionally throws
/// <see cref="System.InvalidOperationException"/> (surfacing as <see cref="System.Reflection.TargetInvocationException"/>
/// through EF's constant-subtree evaluator), so this call can never succeed by falling back to client
/// evaluation the way an ordinary evaluatable expression would.</item>
/// <item><c>matchExpression</c> a plain string field, <c>pattern</c> constant (e.g. <c>Like_literal</c>) — a
/// native <see cref="MongoRegexExpression"/> with <see cref="MongoRegexKind.Like"/>.</item>
/// </list>
/// <para>
/// Declined (falls back — currently to the SAME failure driver-LINQ already produces, so no regression):
/// the 3-argument escape-character overload (no escape-aware pattern conversion implemented yet);
/// <c>pattern</c> not a compile-time constant (e.g. <c>Like_identity</c>'s <c>EF.Functions.Like(c.X, c.X)</c> —
/// a per-document pattern can't be wildcard-converted at translation time, and doing it at Build/per-execution
/// time, like a parameterized <c>StartsWith</c> term, would need threading escape-awareness through
/// <c>PlaceholderTable</c>/<c>MongoPipelineFactory</c> too, deferred); <c>matchExpression</c> resolving to
/// anything other than a bare <see langword="string"/> field (a <c>ToString()</c>/cast-wrapped non-string
/// receiver is a distinct, separately-tracked gap).
/// </para>
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
            // 3 arguments: (DbFunctions, matchExpression, pattern). The 4-argument escape-character overload
            // declines here — see this type's own remarks.
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

        if (!TryResolveMember(matchExpr, out var property, out var fieldPath, out var isOuter)
            || isOuter
            || property.ClrType != typeof(string))
        {
            return false;
        }

        var fieldNode = new MongoFieldExpression(property, fieldPath);
        var termNode = new MongoConstantExpression(patternLiteral, forSerialization: null);
        result = new MongoRegexExpression(fieldNode, MongoRegexKind.Like, termNode, negated: false);
        return true;
    }
}
