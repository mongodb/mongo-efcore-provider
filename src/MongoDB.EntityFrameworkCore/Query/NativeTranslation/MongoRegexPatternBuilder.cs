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
/// Builds escaped/anchored regex text for a <see cref="MongoRegexExpression"/> term; shared by
/// <see cref="MongoQueryLanguageRenderer.RenderRegex"/> (constant) and <see cref="MongoPipelineFactory"/> (per execution).
/// </summary>
internal static class MongoRegexPatternBuilder
{
    /// <summary>
    /// Escapes <paramref name="term"/> and anchors it per <paramref name="kind"/>, matching driver-LINQ v3's
    /// <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> patterns. <see cref="MongoRegexKind.Pattern"/> is returned
    /// unescaped and unanchored: it is already a live .NET regex.
    /// </summary>
    public static string BuildPattern(string term, MongoRegexKind kind)
    {
        if (kind == MongoRegexKind.Like)
            return BuildLikePattern(term);

        // Pattern (Regex.IsMatch(field, pattern)): a live .NET pattern passed to PCRE unchanged, never escaped.
        if (kind == MongoRegexKind.Pattern)
            return term;

        var escaped = Escape(term);
        return kind switch
        {
            MongoRegexKind.StartsWith => "^" + escaped,
            // "\z", not "$": "$" also matches before a trailing "\n", so "a\n" would end with "a".
            MongoRegexKind.EndsWith => escaped + "\\z",
            MongoRegexKind.Contains => escaped,
            MongoRegexKind.Exact => "^" + escaped + "\\z",
            _ => throw new NativeTranslationNotSupportedException($"Unsupported regex kind '{kind}'.")
        };
    }

    /// <summary>
    /// <see cref="Regex.Escape"/> plus NUL: a BSON regex pattern is a C string, so a raw NUL would end the pattern
    /// early, silently dropping the rest of the term (and any anchor). Everything else <see cref="Regex.Escape"/>
    /// leaves alone (<c>]</c>, <c>}</c>, control characters, non-ASCII) is literal to PCRE outside a character class.
    /// </summary>
    private static string Escape(string text)
        => Regex.Escape(text).Replace("\0", "\\x00");

    /// <summary>
    /// Converts a SQL LIKE pattern (<c>%</c>, <c>_</c>) to a whole-string-anchored regex. No escape character
    /// (that overload declines); <c>[</c>/<c>]</c>/<c>^</c> are literal, per ANSI LIKE rather than T-SQL.
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
                _ => Escape(ch.ToString())
            });
        }

        // "\z", not "$": "$" also matches before a trailing "\n", so "ABC\n" would match "AB_".
        pattern.Append("\\z");
        return pattern.ToString();
    }
}
