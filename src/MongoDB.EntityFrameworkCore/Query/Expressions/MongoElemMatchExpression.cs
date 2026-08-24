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

/// <summary>
/// Represents an existential quantifier over an embedded (owned) array field: at least one element of
/// <see cref="ArrayPath"/> satisfies <see cref="ElementPredicate"/>, optionally negated. Renders to
/// <c>$elemMatch</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArrayPath"/> is relative to the ENCLOSING document scope, and
/// <see cref="ElementPredicate"/>'s field paths are relative to the ARRAY ELEMENT — not to the document
/// root. That is what <c>$elemMatch</c> requires, and it is what makes nesting work: an
/// <see cref="MongoElemMatchExpression"/> inside another one carries an element-relative array path of its
/// own.
/// </para>
/// <para>
/// Consequence for <see cref="NativeTranslation.MongoFieldPrefixRewriter"/>: prefixing must apply to
/// <see cref="ArrayPath"/> only. Rewriting the element predicate would mis-address every field inside the
/// <c>$elemMatch</c>.
/// </para>
/// <para>
/// A BARE <c>Any()</c> is deliberately NOT represented by this node: it is exactly <c>Count &gt;= 1</c>, so it
/// is translated as an array-count comparison over <see cref="MongoSizeExpression"/> and renders through the
/// same array-index existence form (<c>{"path.0": {$exists: true}}</c>). Keeping one representation for array
/// cardinality is what makes <c>Any()</c> and <c>Count &gt;= 1</c> structurally identical rather than
/// coincidentally equal, and it is why this node's predicate is non-nullable.
/// </para>
/// </remarks>
internal sealed class MongoElemMatchExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoElemMatchExpression"/>.
    /// </summary>
    /// <param name="arrayPath">
    /// The dotted document path of the embedded array, relative to the enclosing scope (e.g. <c>"Posts"</c>,
    /// or <c>"Home.Notes"</c> when reached through an owned single reference).
    /// </param>
    /// <param name="elementPredicate">
    /// The predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths.
    /// </param>
    /// <param name="negated"><see langword="true"/> for the negated form (<c>!source.Any(...)</c>).</param>
    public MongoElemMatchExpression(string arrayPath, MongoExpression elementPredicate, bool negated)
    {
        ArrayPath = arrayPath;
        ElementPredicate = elementPredicate;
        Negated = negated;
    }

    /// <summary>The dotted document path of the embedded array, relative to the enclosing scope.</summary>
    public string ArrayPath { get; }

    /// <summary>
    /// The predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths.
    /// </summary>
    public MongoExpression ElementPredicate { get; }

    /// <summary><see langword="true"/> for the negated form (<c>!source.Any(...)</c>).</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
