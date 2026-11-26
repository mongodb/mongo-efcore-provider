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

using System;
using System.Text.RegularExpressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Pins the regex text built for <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>/<c>Like</c>. The server uses PCRE and
/// the tests use .NET, but only constructs the two agree on appear here (literal escapes, <c>^</c>, <c>\z</c>,
/// <c>.</c> with the dot-all option the renderer always sets).
/// </summary>
public class MongoRegexPatternBuilderTests
{
    private static bool Matches(string value, string pattern)
        => Regex.IsMatch(value, pattern, RegexOptions.Singleline);

    [Theory]
    [InlineData("report.pdf\n", false)]
    [InlineData("report.pdf\r\n", false)]
    [InlineData("report.pdf ", false)]
    [InlineData("report.pdf", true)]
    [InlineData("a.pdf", true)]
    public void EndsWith_is_anchored_to_the_end_of_the_string_not_before_a_trailing_newline(string value, bool expected)
        => Assert.Equal(expected, Matches(value, MongoRegexPatternBuilder.BuildPattern(".pdf", MongoRegexKind.EndsWith)));

    [Theory]
    [InlineData("ABC", true)]
    [InlineData("AB\n", true)] // "_" is any single character, including a newline
    [InlineData("ABC\n", false)]
    [InlineData("ABC\r\n", false)]
    [InlineData("AB", false)]
    public void Like_is_anchored_to_the_end_of_the_string_not_before_a_trailing_newline(string value, bool expected)
        => Assert.Equal(expected, Matches(value, MongoRegexPatternBuilder.BuildPattern("AB_", MongoRegexKind.Like)));

    [Fact]
    public void Like_percent_spans_newlines()
        => Assert.True(Matches("a\nb\r\nc", MongoRegexPatternBuilder.BuildPattern("a%c", MongoRegexKind.Like)));

    [Theory]
    [InlineData("StartsWith", "^abc")]
    [InlineData("EndsWith", "abc\\z")]
    [InlineData("Contains", "abc")]
    [InlineData("Exact", "^abc\\z")]
    [InlineData("Like", "^abc\\z")]
    public void Anchors_per_kind(string kind, string expected)
        => Assert.Equal(expected, MongoRegexPatternBuilder.BuildPattern("abc", Enum.Parse<MongoRegexKind>(kind)));

    // Every regex metacharacter, whitespace and control character must match only itself, in every kind.
    [Theory]
    [InlineData(@"\")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("?")]
    [InlineData("|")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("^")]
    [InlineData("$")]
    [InlineData(".")]
    [InlineData("#")]
    [InlineData(" ")]
    [InlineData("-")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\f")]
    [InlineData("\v")]
    [InlineData("\0")]
    [InlineData("é")]
    [InlineData("😀")]
    [InlineData("a.b*c+d?e|f(g)h[i]j{1}k^l$m\\n#n o")]
    public void Special_characters_in_the_term_match_only_themselves(string term)
    {
        foreach (var kind in new[]
                 {
                     MongoRegexKind.StartsWith, MongoRegexKind.EndsWith, MongoRegexKind.Contains, MongoRegexKind.Exact
                 })
        {
            var pattern = MongoRegexPatternBuilder.BuildPattern(term, kind);

            Assert.True(Matches("xx" + term + "yy", pattern) == (kind == MongoRegexKind.Contains), $"{kind} 'xx{term}yy'");
            Assert.True(Matches(term, pattern), $"{kind} term itself");
            Assert.False(Matches("zz" + new string('Q', term.Length), pattern), $"{kind} unrelated text");
        }

        // Like: only "%" and "_" are special; everything else is literal.
        if (term is not ("%" or "_"))
        {
            var like = MongoRegexPatternBuilder.BuildPattern(term, MongoRegexKind.Like);
            Assert.True(Matches(term, like), "Like term itself");
            Assert.False(Matches("Q" + term, like), "Like prefixed");
        }
    }

    [Theory]
    [InlineData("StartsWith")]
    [InlineData("EndsWith")]
    [InlineData("Contains")]
    [InlineData("Exact")]
    [InlineData("Like")]
    public void Pattern_never_contains_a_raw_NUL_because_a_BSON_regex_is_a_C_string(string kind)
    {
        var pattern = MongoRegexPatternBuilder.BuildPattern("a\0b", Enum.Parse<MongoRegexKind>(kind));

        Assert.DoesNotContain('\0', pattern);
        Assert.Contains("\\x00", pattern);
    }

    [Fact]
    public void Like_percent_and_underscore_are_wildcards_but_regex_metacharacters_are_literal()
    {
        var pattern = MongoRegexPatternBuilder.BuildPattern("a.%[x]_(", MongoRegexKind.Like);

        Assert.True(Matches("a.zzz[x]Q(", pattern));
        Assert.False(Matches("aXzzz[x]Q(", pattern)); // "." is literal, not a wildcard
        Assert.False(Matches("a.zzzxQ(", pattern)); // "[x]" is literal, not a class
    }

    [Fact]
    public void Pattern_kind_is_passed_through_unchanged()
        => Assert.Equal("a.*\\d$", MongoRegexPatternBuilder.BuildPattern("a.*\\d$", MongoRegexKind.Pattern));
}
