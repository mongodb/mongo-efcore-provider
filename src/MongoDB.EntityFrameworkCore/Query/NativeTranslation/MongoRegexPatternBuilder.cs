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
/// Builds escaped/anchored regex pattern text for a <see cref="MongoRegexExpression"/> term. Shared by
/// <see cref="MongoQueryLanguageRenderer.RenderRegex"/> (constant term) and <see cref="MongoPipelineFactory"/>
/// (parameterized term, escaped per execution).
/// </summary>
internal static class MongoRegexPatternBuilder
{
    /// <summary>
    /// Escapes <paramref name="term"/> and anchors it per <paramref name="kind"/>, matching the pattern driver-LINQ
    /// v3 emits for <c>string.StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>. For <see cref="MongoRegexKind.Pattern"/>
    /// (<c>Regex.IsMatch(field, pattern)</c>), <paramref name="term"/> is returned unescaped and unanchored — it is
    /// already a live .NET regex pattern.
    /// </summary>
    public static string BuildPattern(string term, MongoRegexKind kind)
    {
        if (kind == MongoRegexKind.Like)
            return BuildLikePattern(term);

        // Pattern (Regex.IsMatch(field, pattern)): a live .NET pattern passed to PCRE unchanged, never escaped.
        if (kind == MongoRegexKind.Pattern)
            return term;

        var escaped = Regex.Escape(term);
        return kind switch
        {
            MongoRegexKind.StartsWith => "^" + escaped,
            MongoRegexKind.EndsWith => escaped + "$",
            MongoRegexKind.Contains => escaped,
            // "\z", not "$": "$" also matches before a trailing "\n", so "a\n" would equal "a".
            MongoRegexKind.Exact => "^" + escaped + "\\z",
            _ => throw new NativeTranslationNotSupportedException($"Unsupported regex kind '{kind}'.")
        };
    }

    /// <summary>
    /// Converts a SQL LIKE pattern (<c>%</c>, <c>_</c>) to a whole-string-anchored regex. No escape-character
    /// support (the 3-argument overload declines in <c>MongoExpressionTranslator.Like.cs</c>); <c>[</c>/<c>]</c>/<c>^</c>
    /// are literal, per ANSI LIKE rather than T-SQL.
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
