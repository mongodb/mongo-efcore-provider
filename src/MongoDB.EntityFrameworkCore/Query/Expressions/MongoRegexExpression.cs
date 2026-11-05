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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>The kind of string test: whether the value starts with, ends with, or contains a given term.</summary>
internal enum MongoRegexKind
{
    StartsWith,
    EndsWith,
    Contains,

    /// <summary><c>EF.Functions.Like</c> with a constant pattern. Query ($match) dialect only: there is no <c>$expr</c> rendering.</summary>
    Like,

    /// <summary>
    /// The reversed-argument <c>Regex.IsMatch(input, pattern)</c> shape: a constant input tested against a
    /// field-valued pattern. <see cref="MongoRegexExpression.Field"/> holds the pattern field and
    /// <see cref="MongoRegexExpression.Term"/> the input. Aggregation dialect only (<c>$regexMatch</c>;
    /// <c>$regularExpression</c> needs a literal); a row with an invalid regex makes the server throw.
    /// </summary>
    IsMatch,

    /// <summary>
    /// Whole-string equality (<c>^term\z</c>) against a constant string, with <see cref="MongoRegexExpression.CaseInsensitive"/>.
    /// </summary>
    Exact,

    /// <summary>
    /// <c>Regex.IsMatch(field, pattern)</c>: the term is a live .NET pattern passed to PCRE unchanged (dialect
    /// differences are the caller's); options come from <see cref="MongoRegexExpression.PatternOptions"/>.
    /// A parameter or field term (EF-247) renders only as <c>$expr</c>/<c>$regexMatch</c> because
    /// <c>$regularExpression</c> needs a literal. A null or missing pattern matches nothing (.NET would throw).
    /// </summary>
    Pattern,
}

/// <summary>A string prefix/suffix/substring/pattern test of a field against a term, optionally negated.</summary>
internal sealed class MongoRegexExpression : MongoExpression
{
    /// <summary>Creates a <see cref="MongoRegexExpression"/>.</summary>
    /// <param name="field">
    /// A <c>MongoFieldExpression</c>, or a <c>MongoElementRefExpression</c> for a value with no backing
    /// <c>IProperty</c>; only the document path is read. A computed string (<c>(c.A + "").Contains("1")</c>) has no
    /// document path and renders only via <c>$expr</c>/<c>$regexMatch</c>.
    /// </param>
    /// <param name="kind">The kind of test.</param>
    /// <param name="term">
    /// A string constant/parameter, or a field for a field-to-field test (<c>c.A.StartsWith(c.B)</c>), which renders
    /// only via <c>$indexOfCP</c> inside <c>$expr</c>.
    /// </param>
    /// <param name="negated">Whether the test is negated.</param>
    /// <param name="caseInsensitive">Whether the match ignores case.</param>
    /// <param name="patternOptions">Extra regex options.</param>
    public MongoRegexExpression(
        MongoExpression field, MongoRegexKind kind, MongoExpression term, bool negated, bool caseInsensitive = false,
        string patternOptions = "")
    {
        Field = field;
        Kind = kind;
        Term = term;
        Negated = negated;
        CaseInsensitive = caseInsensitive;
        PatternOptions = patternOptions;
    }

    /// <summary>The document field being tested; see the constructor.</summary>
    public new MongoExpression Field { get; }

    /// <summary>The kind of test.</summary>
    public MongoRegexKind Kind { get; }

    /// <summary>The search term; see the constructor.</summary>
    public MongoExpression Term { get; }

    /// <summary><see langword="true"/> for a negated match (<c>!s.StartsWith(...)</c>).</summary>
    public bool Negated { get; }

    /// <summary><see langword="true"/> for <c>StringComparison.OrdinalIgnoreCase</c>.</summary>
    public bool CaseInsensitive { get; }

    /// <summary>The regex options string (e.g. <c>"im"</c>) for <see cref="MongoRegexKind.Pattern"/>; unused otherwise.</summary>
    public string PatternOptions { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
