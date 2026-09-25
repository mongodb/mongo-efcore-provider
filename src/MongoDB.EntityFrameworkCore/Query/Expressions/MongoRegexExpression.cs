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
    /// <c>EF.Functions.Like(matchExpression, pattern)</c> against a compile-time-constant SQL LIKE pattern
    /// (<c>%</c>/<c>_</c> wildcards) — see
    /// <see cref="NativeTranslation.MongoRegexPatternBuilder.BuildPattern"/>'s <c>Like</c>
    /// arm. Scoped to the query ($match) dialect only: <c>MongoAggregationExpressionRenderer.CanRender</c>
    /// declines a <c>Like</c>-kind regex rather than admitting it into the $expr dialect, since there is no
    /// $expr rendering for it (a Like pattern needs wildcard-to-regex conversion, not a literal substring
    /// search like $indexOfCP).
    /// </summary>
    Like,

    /// <summary>
    /// The REVERSED-argument shape of <c>System.Text.RegularExpressions.Regex.IsMatch(input, pattern)</c> —
    /// a compile-time-constant <c>input</c> tested against a document-field-valued <c>pattern</c> (e.g.
    /// <c>Regex.IsMatch("Seattle", o.String)</c>). Unlike every other <see cref="MongoRegexKind"/> member,
    /// <see cref="MongoRegexExpression.Field"/> here holds the resolved PATTERN field (not the value under
    /// test) and <see cref="MongoRegexExpression.Term"/> holds the fixed input string — see
    /// <see cref="NativeTranslation.MongoExpressionTranslator"/>'s <c>TryTranslateRegexIsMatch</c> for why the
    /// roles are swapped relative to StartsWith/EndsWith/Contains/Like.
    /// <para>
    /// Aggregation-expression-dialect ONLY: MongoDB's <c>$regularExpression</c> query-dialect BSON type
    /// requires a literal pattern, never a field, so this kind has no query-dialect form at all — see
    /// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>'s and <c>RenderNode</c>'s dedicated
    /// <c>IsMatch</c> exclusions. It renders instead via the aggregation expression <c>$regexMatch</c>
    /// operator, whose <c>regex</c> operand — unlike <c>$regularExpression</c>'s — may itself be a
    /// field-valued expression.
    /// </para>
    /// <para>
    /// <b>Malformed-pattern risk is inherent, not a bug to guard against.</b> Because the pattern is sourced
    /// from a document field rather than validated once at C# compile/translate time, a row whose pattern
    /// field holds an invalid regex causes <c>$regexMatch</c> to throw a genuine per-document server error at
    /// execution time. This is unavoidable for a field-valued pattern (the forward, constant-pattern shape
    /// has no equivalent risk — its pattern is a fixed, already-valid .NET <see cref="System.Text.RegularExpressions.Regex"/>
    /// literal) and is not specific to this provider: the same query would fail the same way against any
    /// driver capable of expressing it.
    /// </para>
    /// </summary>
    IsMatch
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
    /// The document field being tested: a <c>MongoFieldExpression</c> for an ordinary property, or a
    /// <c>MongoElementRefExpression</c> for a value with no backing <c>IProperty</c> — e.g. a projected
    /// <c>Distinct()</c>'s own COMPUTED alias (EF-322 gap-2). Both render identically here: only the document
    /// path is ever read (see <c>MongoQueryLanguageRenderer.RenderRegex</c>), never property metadata.
    /// </param>
    /// <param name="kind">The kind of regex test to perform (StartsWith, EndsWith, or Contains).</param>
    /// <param name="term">
    /// The search term: a <c>MongoConstantExpression</c> or <c>MongoParameterExpression</c> of string, or a
    /// <c>MongoFieldExpression</c> for a field-to-field test (e.g. <c>c.A.StartsWith(c.B)</c>) — the latter has
    /// no query-dialect form and is rendered only via <c>MongoAggregationExpressionRenderer</c>'s <c>$indexOfCP</c>
    /// form inside <c>$expr</c>.
    /// </param>
    /// <param name="negated"><see langword="true"/> for a negated match (<c>!s.StartsWith(...)</c>).</param>
    /// <param name="caseInsensitive">
    /// <see langword="true"/> for a case-insensitive match (<c>StringComparison.OrdinalIgnoreCase</c>).
    /// <see langword="false"/> (the default, matching every pre-existing construction site) for the ordinal
    /// case-sensitive form.
    /// </param>
    public MongoRegexExpression(
        MongoExpression field, MongoRegexKind kind, MongoExpression term, bool negated, bool caseInsensitive = false)
    {
        Field = field;
        Kind = kind;
        Term = term;
        Negated = negated;
        CaseInsensitive = caseInsensitive;
    }

    /// <summary>
    /// The document field being tested — a <c>MongoFieldExpression</c> or a <c>MongoElementRefExpression</c>;
    /// see the constructor's own remarks.
    /// </summary>
    // 'new' hides the inherited Expression.Field(...) method; used for semantic clarity.
    public new MongoExpression Field { get; }

    /// <summary>The kind of regex test to perform (StartsWith, EndsWith, or Contains).</summary>
    public MongoRegexKind Kind { get; }

    /// <summary>
    /// The search term: a <c>MongoConstantExpression</c> or <c>MongoParameterExpression</c> of string, or a
    /// <c>MongoFieldExpression</c> for a field-to-field test (e.g. <c>c.A.StartsWith(c.B)</c>) — the latter has
    /// no query-dialect form and is rendered only via <c>MongoAggregationExpressionRenderer</c>'s <c>$indexOfCP</c>
    /// form inside <c>$expr</c>.
    /// </summary>
    public MongoExpression Term { get; }

    /// <summary><see langword="true"/> for a negated match (<c>!s.StartsWith(...)</c>).</summary>
    public bool Negated { get; }

    /// <summary>
    /// <see langword="true"/> for a case-insensitive match (<c>StringComparison.OrdinalIgnoreCase</c>).
    /// <see langword="false"/> (the default, matching every pre-existing construction site) for the ordinal
    /// case-sensitive form.
    /// </summary>
    public bool CaseInsensitive { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
