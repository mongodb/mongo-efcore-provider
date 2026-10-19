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
/// A correlated quantifier over an owned array — <c>b.Posts.Any(p =&gt; p.X == b.Y)</c> or <c>All</c> — whose
/// element predicate references the enclosing entity. Renders as <c>$anyElementTrue</c>/<c>$allElementsTrue</c>
/// over a <c>$map</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only for the correlated case: <c>$elemMatch</c> cannot reference the enclosing document, but a <c>$map</c>'s
/// <c>in</c> expression can. Uncorrelated quantifiers use the more index-friendly
/// <see cref="MongoElemMatchExpression"/>.
/// </para>
/// <para>
/// <c>All</c> needs no negation at construction (<c>$allElementsTrue</c> is already "for all"), so
/// <see cref="ElementPredicate"/> is translated directly for both kinds.
/// <see cref="NativeTranslation.MongoExpressionNegator"/> negates this node by De Morgan (flip <see cref="Kind"/>,
/// negate the predicate), since the two operators are exact duals.
/// </para>
/// <para>
/// Aggregation-expression only: <see cref="NativeTranslation.MongoQueryLanguageRenderer.IsQueryDialectRenderable"/>
/// declines it, and the renderer's generic <c>$expr</c> fallback wraps it.
/// </para>
/// </remarks>
internal sealed class MongoQuantifierExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoQuantifierExpression"/>.
    /// </summary>
    /// <param name="arrayPath">
    /// Path of the embedded array relative to the outer document root (e.g. <c>"Posts"</c>) — always
    /// outer-relative, unlike <see cref="MongoElemMatchExpression.ArrayPath"/>.
    /// </param>
    /// <param name="elementPredicate">
    /// Per-element predicate: element-relative paths (rendered against the <c>$map</c> variable) plus
    /// <see cref="MongoOuterFieldExpression"/> paths for the enclosing entity.
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
