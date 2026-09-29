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

    /// <summary>
    /// <c>EF.Functions.Like(matchExpression, pattern)</c> with a constant SQL LIKE pattern (see
    /// <see cref="NativeTranslation.MongoRegexPatternBuilder.BuildPattern"/>). Query ($match) dialect only;
    /// <c>MongoAggregationExpressionRenderer.CanRender</c> declines it since there's no <c>$expr</c> rendering.
    /// </summary>
    Like,

    /// <summary>
    /// The reversed-argument <c>Regex.IsMatch(input, pattern)</c> shape: a constant input tested against a
    /// field-valued pattern (<c>Regex.IsMatch("Seattle", o.String)</c>). Here <see cref="MongoRegexExpression.Field"/>
    /// holds the pattern field and <see cref="MongoRegexExpression.Term"/> the input (see
    /// <see cref="NativeTranslation.MongoExpressionTranslator"/>'s <c>TryTranslateRegexIsMatch</c>).
    /// <para>
    /// Aggregation dialect only (<c>$regexMatch</c>): <c>$regularExpression</c> requires a literal pattern. A row
    /// whose pattern field holds an invalid regex makes the server throw at execution time; that's inherent to a
    /// field-valued pattern.
    /// </para>
    /// </summary>
    IsMatch,

    /// <summary>
    /// Whole-string equality (<c>^term\z</c>) against a constant string, used for <c>Equals(…, OrdinalIgnoreCase)</c>
    /// and <c>ToLower()/ToUpper() == constant</c> with <see cref="MongoRegexExpression.CaseInsensitive"/>.
    /// </summary>
    Exact,

    /// <summary>
    /// <c>Regex.IsMatch(field, pattern)</c>: the term is a live .NET pattern passed to PCRE unchanged (dialect
    /// differences, e.g. <c>\p{...}</c> classes, are the caller's). Options come from
    /// <see cref="MongoRegexExpression.PatternOptions"/>.
    /// </summary>
    Pattern,
}

/// <summary>
/// Represents a string prefix/suffix/substring test that determines whether a field's value starts with,
/// ends with, or contains a given term, optionally negated.
/// </summary>
internal sealed class MongoRegexExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoRegexExpression"/>.
    /// </summary>
    /// <param name="field">
    /// The document field being tested: a <c>MongoFieldExpression</c>, or a <c>MongoElementRefExpression</c> for a
    /// value with no backing <c>IProperty</c> (e.g. a projected <c>Distinct()</c>'s computed alias). Only the
    /// document path is read (see <c>MongoQueryLanguageRenderer.RenderRegex</c>).
    /// </param>
    /// <param name="kind">The kind of regex test to perform (StartsWith, EndsWith, or Contains).</param>
    /// <param name="term">
    /// A string <c>MongoConstantExpression</c>/<c>MongoParameterExpression</c>, or a <c>MongoFieldExpression</c> for a
    /// field-to-field test (<c>c.A.StartsWith(c.B)</c>), which renders only via <c>$indexOfCP</c> inside <c>$expr</c>.
    /// </param>
    /// <param name="negated"><see langword="true"/> for a negated match (<c>!s.StartsWith(...)</c>).</param>
    /// <param name="caseInsensitive">
    /// <see langword="true"/> for <c>StringComparison.OrdinalIgnoreCase</c>; <see langword="false"/> (default) for
    /// ordinal case-sensitive.
    /// </param>
    /// <param name="patternOptions">
    /// The <c>$regularExpression</c>/<c>$regexMatch</c> options string for <see cref="MongoRegexKind.Pattern"/>;
    /// unused for every other kind.
    /// </param>
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

    /// <summary>
    /// The document field being tested; see the constructor.
    /// </summary>
    // 'new' hides the inherited Expression.Field(...) method; used for semantic clarity.
    public new MongoExpression Field { get; }

    /// <summary>The kind of regex test to perform (StartsWith, EndsWith, or Contains).</summary>
    public MongoRegexKind Kind { get; }

    /// <summary>
    /// The search term: a string constant/parameter, or a field for a field-to-field test (<c>$expr</c> only).
    /// </summary>
    public MongoExpression Term { get; }

    /// <summary><see langword="true"/> for a negated match (<c>!s.StartsWith(...)</c>).</summary>
    public bool Negated { get; }

    /// <summary>
    /// <see langword="true"/> for <c>StringComparison.OrdinalIgnoreCase</c>; <see langword="false"/> for ordinal.
    /// </summary>
    public bool CaseInsensitive { get; }

    /// <summary>
    /// The <c>$regularExpression</c>/<c>$regexMatch</c> options string for <see cref="MongoRegexKind.Pattern"/>
    /// (e.g. <c>"im"</c>); unused for every other kind.
    /// </summary>
    public string PatternOptions { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
