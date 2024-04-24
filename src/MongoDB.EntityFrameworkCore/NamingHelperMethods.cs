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

using System.Globalization;
using System.Text.RegularExpressions;

namespace MongoDB.EntityFrameworkCore;

/// <summary>
/// A variety of helper methods the MongoDB provider users for creating names.
/// </summary>
public static partial class NamingHelperMethods
{
    /// <summary>
    /// Converts a given string to camel case.
    /// </summary>
    /// <param name="input">The input string to convert.</param>
    /// <param name="culture">The culture to use in upper-casing and lower-casing letters.</param>
    /// <returns>The cleaned camel-cased string.</returns>
    /// <remarks>Word boundaries are considered spaces, non-alphanumeric, a transition from
    /// lower to upper, and a transition from number to non-number.</remarks>
    public static string ToCamelCase(
        this string input,
        CultureInfo culture)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var words = WordSplitRegex.Split(input);

        var result = new char[input.Length];
        var outIndex = 0;

        for (var inIndex = 0; inIndex < words.Length; inIndex++)
        {
            var w = words[inIndex];
            if (w.Length == 0) continue;

            if (inIndex == 0)
            {
                result[outIndex] = char.ToLower(w[0], culture);
            }
            else
            {
                result[outIndex] = char.ToUpper(w[0], culture);
            }

            w.ToLower(culture).CopyTo(1, result, outIndex + 1, w.Length - 1);

            outIndex += w.Length;
        }

        return new string(result, 0, outIndex);
    }

    /// <summary>
    /// Converts a given string to title case.
    /// </summary>
    /// <param name="input">The input string to convert.</param>
    /// <param name="culture">The culture to use in upper-casing and lower-casing letters.</param>
    /// <returns>The cleaned title-cased string.</returns>
    /// <remarks>Word boundaries are considered spaces, non-alphanumeric, a transition from
    /// lower to upper, and a transition from number to non-number.</remarks>
    public static string ToTitleCase(
        this string input,
        CultureInfo culture)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var words = WordSplitRegex.Split(input);

        var result = new char[input.Length];
        var outIndex = 0;

        for (var inIndex = 0; inIndex < words.Length; inIndex++)
        {
            var w = words[inIndex];
            if (w.Length == 0) continue;

            result[outIndex] = char.ToUpper(w[0], culture);
            w.ToLower(culture).CopyTo(1, result, outIndex + 1, w.Length - 1);

            outIndex += w.Length;
        }

        return new string(result, 0, outIndex);
    }

    // Find word boundaries.
    // Word boundaries are considered spaces, non-alphanumeric, a transition from lower to upper,
    // a transition from multiple uppers to lowercase, and a transition from number to non-number.
    private static readonly Regex WordSplitRegex =
        new(@"(?<=[a-z])(?=[A-Z])|[\s\p{P}]|(?<=\p{Lu})(?=\p{Lu}\p{Ll})|(?<=\p{N})(?=\P{N})");
}
