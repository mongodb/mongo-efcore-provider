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
    Contains
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
    public MongoRegexExpression(MongoExpression field, MongoRegexKind kind, MongoExpression term, bool negated)
    {
        Field = field;
        Kind = kind;
        Term = term;
        Negated = negated;
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

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
