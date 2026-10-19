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
/// Existential quantifier over an embedded (owned) array field, optionally negated. Renders to <c>$elemMatch</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArrayPath"/> is relative to the enclosing document scope; <see cref="ElementPredicate"/>'s field
/// paths are relative to the array element, as <c>$elemMatch</c> requires (and which makes nesting work). So
/// <see cref="NativeTranslation.MongoFieldPrefixRewriter"/> must prefix <see cref="ArrayPath"/> only.
/// </para>
/// <para>
/// A bare <c>Any()</c> is not represented here: it is translated as <c>Count &gt;= 1</c> over
/// <see cref="MongoSizeExpression"/>, keeping one representation for array cardinality. Hence the predicate is
/// non-nullable.
/// </para>
/// </remarks>
internal sealed class MongoElemMatchExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoElemMatchExpression"/>.
    /// </summary>
    /// <param name="arrayPath">
    /// Dotted path of the embedded array, relative to the enclosing scope (e.g. <c>"Home.Notes"</c>).
    /// </param>
    /// <param name="elementPredicate">The element predicate, with element-relative field paths.</param>
    /// <param name="negated"><see langword="true"/> for <c>!source.Any(...)</c>.</param>
    public MongoElemMatchExpression(string arrayPath, MongoExpression elementPredicate, bool negated)
    {
        ArrayPath = arrayPath;
        ElementPredicate = elementPredicate;
        Negated = negated;
    }

    /// <summary>The dotted document path of the embedded array, relative to the enclosing scope.</summary>
    public string ArrayPath { get; }

    /// <summary>The element predicate, with element-relative field paths.</summary>
    public MongoExpression ElementPredicate { get; }

    /// <summary><see langword="true"/> for <c>!source.Any(...)</c>.</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
