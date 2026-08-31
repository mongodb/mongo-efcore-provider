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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a CORRELATED quantifier over an owned (embedded) array — <c>b.Posts.Any(p =&gt; p.X == b.Y)</c>
/// or the <c>All</c> equivalent — whose element predicate references the immediately enclosing entity.
/// Renders as <c>$anyElementTrue</c>/<c>$allElementsTrue</c> over a <c>$map</c>.
/// </summary>
/// <remarks>
/// <para>
/// The UNCORRELATED quantifier path is unchanged: it still uses <see cref="MongoElemMatchExpression"/>
/// (<c>$elemMatch</c>), which is more index-friendly. This node exists ONLY for the correlated case, where
/// <c>$elemMatch</c> cannot reference the enclosing document at all — <c>$anyElementTrue</c>/
/// <c>$allElementsTrue</c> over a <c>$map</c> is the only MQL form that can, since the <c>$map</c>'s own
/// <c>in</c> expression is an ordinary aggregation expression with the enclosing document still reachable.
/// </para>
/// <para>
/// UNLIKE the uncorrelated <c>All</c> path (a NEGATED <see cref="MongoElemMatchExpression"/> over the exact
/// complement, since <c>$elemMatch</c> has no native "for all" form), <c>All</c> here needs NO negation AT
/// CONSTRUCTION TIME: <c>$allElementsTrue</c> is itself a "for all" operator, so <see cref="ElementPredicate"/>
/// is the predicate translated DIRECTLY, exactly as <see cref="Kind"/><c> == Any</c>'s is.
/// <see cref="NativeTranslation.MongoExpressionNegator"/> DOES have its own case for this node, though (added
/// once a root-level <c>!All(pred)</c> containing a nested correlated <c>Any</c> needed to negate cleanly): it
/// De Morgans a whole <see cref="MongoQuantifierExpression"/> directly — flips <see cref="Kind"/> between
/// <c>Any</c>/<c>All</c> and negates <see cref="ElementPredicate"/> in place — as a narrow, deliberate
/// exception to its own query-dialect gate, since <c>$anyElementTrue</c>/<c>$allElementsTrue</c> are each
/// other's exact logical duals. See <see cref="NativeTranslation.MongoExpressionNegator"/>'s own remarks for
/// the exact mechanism and why it is safe outside <c>$elemMatch</c>.
/// </para>
/// <para>
/// Aggregation-expression-ONLY — no query-dialect form. It has no query-dialect analogue at all (unlike
/// <see cref="MongoElemMatchExpression"/>, which IS query-dialect), so
/// <see cref="NativeTranslation.MongoQueryLanguageRenderer.IsQueryDialectRenderable"/> declines it
/// unconditionally, and the renderer's own top-level "no dialect form → wrap in <c>$expr</c>" fallback
/// (<see cref="NativeTranslation.MongoQueryLanguageRenderer"/>'s <c>RenderNode</c> catch-all) wraps it
/// automatically — no bespoke <c>$expr</c>-wrapping code is needed for this node.
/// </para>
/// </remarks>
internal sealed class MongoQuantifierExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoQuantifierExpression"/>.
    /// </summary>
    /// <param name="arrayPath">
    /// The dotted document path of the embedded array, relative to the enclosing (outer) document root —
    /// e.g. <c>"Posts"</c>. Always OUTER-relative, unlike <see cref="MongoElemMatchExpression.ArrayPath"/>,
    /// because this node exists specifically for the correlated case.
    /// </param>
    /// <param name="elementPredicate">
    /// The predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths for the
    /// inner scope (rendered against the <c>$map</c>'s own <c>as</c> variable) and OUTER-relative
    /// (<see cref="MongoOuterFieldExpression"/>) field paths for anything reaching the enclosing entity.
    /// </param>
    /// <param name="kind">Whether this is an <c>Any</c> or <c>All</c> quantifier.</param>
    public MongoQuantifierExpression(MongoElementRefExpression arrayPath, MongoExpression elementPredicate, MongoExpressionTranslator.MongoQuantifierKind kind)
    {
        ArrayPath = arrayPath;
        ElementPredicate = elementPredicate;
        Kind = kind;
    }

    /// <summary>The dotted document path of the embedded array, relative to the enclosing (outer) document root.</summary>
    public MongoElementRefExpression ArrayPath { get; }

    /// <summary>The predicate each candidate element is tested against.</summary>
    public MongoExpression ElementPredicate { get; }

    /// <summary>Whether this is an <c>Any</c> or <c>All</c> quantifier.</summary>
    public MongoExpressionTranslator.MongoQuantifierKind Kind { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
