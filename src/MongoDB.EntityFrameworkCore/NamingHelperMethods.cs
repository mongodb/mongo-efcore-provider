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

using System.Buffers;
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

        var words = WordSplitRegex().Split(input);

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
    public static string ToTitleCase(this string input, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(input)) return input;

        Span<char> initialBuffer = stackalloc char[512];

        if (input.Length > initialBuffer.Length) return ToTitleCaseLarge(input, culture);

        var written = ToTitleCaseInternal(input.AsSpan(), initialBuffer, culture);
        return new string(initialBuffer[..written]);
    }

    private static string ToTitleCaseLarge(string input, CultureInfo culture)
    {
        var rentedArray = ArrayPool<char>.Shared.Rent(input.Length);
        try
        {
            var written = ToTitleCaseInternal(input.AsSpan(), rentedArray, culture);
            return new string(rentedArray, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedArray);
        }
    }

    private static int ToTitleCaseInternal(ReadOnlySpan<char> input, Span<char> output, CultureInfo culture)
    {
        var outIndex = 0;
        var lastIndex = 0;

        foreach (var match in WordSplitRegex.EnumerateMatches(input))
        {
            var wordLength = match.Index - lastIndex;
            if (wordLength > 0)
                ProcessWord(input.Slice(lastIndex, wordLength), output[outIndex..], culture,
                    ref outIndex);

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < input.Length)
            ProcessWord(input[lastIndex..], output[outIndex..], culture, ref outIndex);

        return outIndex;
    }

    private static void ProcessWord(ReadOnlySpan<char> word, Span<char> output, CultureInfo culture,
        ref int outIndex)
    {
        output[0] = char.ToUpper(word[0], culture);
        for (var i = 1; i < word.Length; i++) output[i] = char.ToLower(word[i], culture);
        outIndex += word.Length;
    }

    // Find word boundaries.
    // Word boundaries are considered spaces, non-alphanumeric, a transition from lower to upper,
    // a transition from multiple uppers to lowercase, and a transition from number to non-number.
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|[\s\p{P}]|(?<=\p{Lu})(?=\p{Lu}\p{Ll})|(?<=\p{N})(?=\P{N})")]
    private static partial Regex WordSplitRegex();
}
