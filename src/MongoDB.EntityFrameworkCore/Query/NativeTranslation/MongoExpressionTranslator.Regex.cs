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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>Regex.IsMatch</c>: forward <c>(field, pattern[, options])</c>
/// (<see cref="MongoRegexKind.Pattern"/>) and reversed <c>(constantInput, fieldPattern)</c>
/// (<see cref="MongoRegexKind.IsMatch"/>).
/// </summary>
/// <remarks>
/// Forward: the pattern is a constant, a query parameter or a string field; the latter two render via
/// <c>$expr</c>/<c>$regexMatch</c> only. The .NET pattern is passed to PCRE unchanged (dialect differences are the
/// caller's); flags that <see cref="MapRegexOptions"/> can't map decline. Reversed: a field-valued pattern needs the
/// aggregation <c>$regexMatch</c> (query-dialect <c>$regularExpression</c> requires a literal), hence the dedicated
/// aggregation-only kind; options may only be <see cref="RegexOptions.None"/>/<see cref="RegexOptions.IgnoreCase"/>.
/// A malformed field-valued pattern is an inherent server-error risk (see <see cref="MongoRegexKind.IsMatch"/>).
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    /// <summary>
    /// Whether a string property can be searched by a regex (<c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>/
    /// <c>Like</c>/<c>Regex.IsMatch</c>/case-folded equality). A regex runs on the STORED value, so only a BSON string
    /// qualifies; with an <c>ObjectId</c> representation a regex silently matches nothing. A value converter is
    /// deliberately NOT gated: converted-but-string-stored properties already behave identically on every path.
    /// </summary>
    internal static bool IsRegexSearchableString(IReadOnlyProperty property)
        => property.ClrType == typeof(string)
           && (property.GetBsonRepresentation() is null or { BsonType: BsonType.String });

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
            return false; // a field-valued input is not supported

        var caseInsensitive = false;
        if (call.Arguments.Count == 3)
        {
            if (Unwrap(call.Arguments[2]) is not ConstantExpression { Value: RegexOptions options })
                return false;

            if (options is not (RegexOptions.None or RegexOptions.IgnoreCase))
                return false; // other flags have no faithful $regexMatch equivalent here

            caseInsensitive = options == RegexOptions.IgnoreCase;
        }

        if (!TryResolveInnerStringField(Unwrap(call.Arguments[1]), out var fieldNode))
            return false;

        var termNode = new MongoConstantExpression(inputLiteral, forSerialization: null);
        result = new MongoRegexExpression(fieldNode, MongoRegexKind.IsMatch, termNode, negated: false, caseInsensitive);
        return true;
    }

    private bool TryTranslateForwardRegexIsMatch(MethodCallExpression call, out MongoExpression? result)
    {
        result = null;

        if (!TryResolveInnerStringField(Unwrap(call.Arguments[0]), out var fieldNode))
            return false;

        // EF-247: only a constant pattern can use the query dialect ($regularExpression needs a literal); a parameter
        // or field routes to $expr/$regexMatch. A parameter is never a regex placeholder here (that machinery builds
        // patterns from StartsWith/Contains terms): the raw pattern is rendered as a string literal.
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
