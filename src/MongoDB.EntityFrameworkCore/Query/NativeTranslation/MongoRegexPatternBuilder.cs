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

using System.Text.RegularExpressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Builds the escaped/anchored regex pattern text for a <see cref="MongoRegexExpression"/>'s search term,
/// shared by <see cref="MongoQueryLanguageRenderer.RenderRegex"/> (a constant term, escaped at render/compile
/// time) and <see cref="MongoPipelineFactory"/> (a parameterized term, escaped at Build/per-execution time —
/// the term's actual value isn't known until then).
/// </summary>
internal static class MongoRegexPatternBuilder
{
    /// <summary>
    /// Escapes <paramref name="term"/> and wraps it with the anchors matching <paramref name="kind"/>,
    /// producing the same pattern text the driver-LINQ v3 provider emits for
    /// <c>string.StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>.
    /// </summary>
    public static string BuildPattern(string term, MongoRegexKind kind)
    {
        if (kind == MongoRegexKind.Like)
            return BuildLikePattern(term);

        var escaped = Regex.Escape(term);
        return kind switch
        {
            MongoRegexKind.StartsWith => "^" + escaped,
            MongoRegexKind.EndsWith => escaped + "$",
            MongoRegexKind.Contains => escaped,
            _ => throw new NativeTranslationNotSupportedException($"Unsupported regex kind '{kind}'.")
        };
    }

    /// <summary>
    /// Converts a SQL LIKE pattern (<c>%</c> = any run of characters, <c>_</c> = any single character, every
    /// other character literal) into a whole-string-anchored regex. No escape-character support — a LIKE call
    /// using the 3-argument (escape-character) overload declines before reaching here (see
    /// <c>MongoExpressionTranslator.Like.cs</c>); <c>[</c>/<c>]</c>/<c>^</c> character-class wildcards (SQL
    /// Server-specific, not part of the pattern used by any currently-supported shape) are treated as literal
    /// characters, matching ANSI LIKE rather than T-SQL's extended syntax.
    /// </summary>
    private static string BuildLikePattern(string term)
    {
        var pattern = new System.Text.StringBuilder("^");
        foreach (var ch in term)
        {
            pattern.Append(ch switch
            {
                '%' => ".*",
                '_' => ".",
                _ => Regex.Escape(ch.ToString())
            });
        }

        pattern.Append('$');
        return pattern.ToString();
    }
}
