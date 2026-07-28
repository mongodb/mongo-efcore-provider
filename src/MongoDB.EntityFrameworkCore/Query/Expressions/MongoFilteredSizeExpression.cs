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
/// Represents the element count of an owned (embedded) array field FILTERED by a per-element predicate —
/// <c>b.Posts.Count(p =&gt; p.Rank &gt; 0)</c> — rendering as <c>{ $size: { $filter: … } }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is deliberately a SIBLING of <see cref="MongoSizeExpression"/> rather than a flag on it, and the
/// reason is silent wrong data.</b> Four sites match on <c>is MongoSizeExpression</c>, and three of them must
/// NOT fire for a filtered count:
/// <c>MongoQueryLanguageRenderer.TryRenderSizeComparison</c> would render an integer-constant comparison as an
/// array-index existence test (<c>{"Posts.2": {$exists: true}}</c>) — which answers the UNFILTERED count's
/// question, i.e. the wrong rows, with no error;
/// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c> would admit it inside <c>$elemMatch</c>, where
/// <c>$expr</c> is a hard server error;
/// and <c>MongoExpressionNegator</c> would INVERT the operator, which is the exact complement only because the
/// rendered <c>$exists</c> form partitions the value space — the <c>$expr</c> form's operators do not.
/// As a distinct type all three fail CLOSED by construction: a pattern naming
/// <see cref="MongoSizeExpression"/> simply does not match. With a flag, each would be wrong by default and
/// right only if a future editor remembered a guard.
/// </para>
/// <para>
/// There is no <c>NullSafe</c> flag. <see cref="MongoSizeExpression"/> carries one because its unfiltered form
/// is shared with the projected reference-collection count, whose array is a <c>$lookup</c> output and therefore
/// always present. A filtered count has no such analogue, so the <c>$ifNull</c> wrap is unconditional —
/// <c>$size</c>/<c>$filter</c> over a MISSING or explicitly-null array is a hard server error that aborts the
/// whole aggregate, not a wrong answer.
/// </para>
/// <para>
/// <see cref="ElementPredicate"/>'s field paths are ELEMENT-relative by construction (it is translated by a
/// fresh element-scoped <c>MongoExpressionTranslator</c>), which is what lets the renderer address them through
/// the <c>$filter</c> variable — and why <c>MongoFieldPrefixRewriter</c> must prefix
/// <see cref="ArrayPath"/> only, exactly as it does for <see cref="MongoElemMatchExpression"/>.
/// </para>
/// </remarks>
internal sealed class MongoFilteredSizeExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoFilteredSizeExpression"/> over the named array field, filtered by the
    /// given element predicate.
    /// </summary>
    /// <param name="arrayPath">
    /// The array's dotted document path, relative to the enclosing scope (e.g. <c>"Posts"</c>, or
    /// <c>"Home.Notes"</c> when reached through an owned single reference).
    /// </param>
    /// <param name="elementPredicate">
    /// The per-element predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths.
    /// </param>
    /// <param name="type">The CLR type of the resulting count (typically <see cref="int"/> or <see cref="long"/>).</param>
    public MongoFilteredSizeExpression(string arrayPath, MongoExpression elementPredicate, Type type)
    {
        ArrayPath = arrayPath;
        ElementPredicate = elementPredicate;
        Type = type;
    }

    /// <summary>The array's dotted document path, relative to the enclosing scope.</summary>
    public string ArrayPath { get; }

    /// <summary>The per-element predicate, with ELEMENT-relative field paths.</summary>
    public MongoExpression ElementPredicate { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
