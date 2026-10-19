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
/// Count of an owned array filtered by a per-element predicate — <c>b.Posts.Count(p =&gt; p.Rank &gt; 0)</c> —
/// rendered as <c>{ $size: { $filter: … } }</c>.
/// </summary>
/// <remarks>
/// A separate type, not a flag on <see cref="MongoSizeExpression"/>, so sites matching <c>is MongoSizeExpression</c>
/// fail closed: <c>TryRenderSizeComparison</c> would emit an unfiltered <c>$exists</c> test (wrong rows),
/// <c>IsQueryDialectRenderable</c> would admit it inside <c>$elemMatch</c> (server error on <c>$expr</c>), and the
/// negator's operator inversion is only a true complement for the <c>$exists</c> form. The <c>$ifNull</c> wrap is
/// unconditional: <c>$size</c>/<c>$filter</c> over a missing or null array aborts the aggregate.
/// <see cref="ElementPredicate"/> paths are element-relative, so <c>MongoFieldPrefixRewriter</c> must prefix only
/// <see cref="ArrayPath"/>.
/// </remarks>
internal sealed class MongoFilteredSizeExpression : MongoExpression
{
    public MongoFilteredSizeExpression(string arrayPath, MongoExpression elementPredicate, Type type)
    {
        ArrayPath = arrayPath;
        ElementPredicate = elementPredicate;
        Type = type;
    }

    /// <summary>The array's dotted document path, relative to the enclosing scope.</summary>
    public string ArrayPath { get; }

    /// <summary>The per-element predicate, with element-relative field paths.</summary>
    public MongoExpression ElementPredicate { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
