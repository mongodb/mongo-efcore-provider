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
/// A ternary conditional (<c>test ? ifTrue : ifFalse</c>), rendered in the aggregation-expression dialect as
/// <c>$cond</c>.
/// </summary>
/// <remarks>
/// <see cref="Test"/> is always rendered via <c>MongoAggregationExpressionRenderer.Render</c> directly, never
/// via <c>MongoQueryLanguageRenderer.RenderNode</c>'s query/aggregation dual-dialect dispatch — a
/// <c>$cond.if</c> lives inside <c>$project</c>'s expression context, where the query (<c>$match</c>) dialect
/// is never valid, the same rule that already governs everything nested inside <c>$expr</c>.
/// <para>
/// This node is deliberately NOT admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c> — it
/// has no query-dialect form, and <c>$expr</c> (which is what a native <c>$project</c>/<c>$match</c> would
/// need to wrap it in) is a hard server error inside <c>$elemMatch</c>.
/// </para>
/// </remarks>
internal sealed class MongoConditionalExpression(MongoExpression test, MongoExpression ifTrue, MongoExpression ifFalse)
    : MongoExpression
{
    /// <summary>The boolean condition.</summary>
    public MongoExpression Test { get; } = test;

    /// <summary>The value when <see cref="Test"/> is true.</summary>
    public MongoExpression IfTrue { get; } = ifTrue;

    /// <summary>The value when <see cref="Test"/> is false.</summary>
    public MongoExpression IfFalse { get; } = ifFalse;

    /// <inheritdoc />
    /// <remarks>
    /// Prefers <see cref="IfFalse"/>'s type when <see cref="IfTrue"/> is a null-valued
    /// <see cref="MongoConstantExpression"/> — that branch's own <c>.Type</c> falls back to <c>typeof(object)</c>
    /// (it carries no other type information), which would otherwise misreport the conditional's overall type
    /// as <c>object</c> instead of the meaningful type from the other branch. This is a live path:
    /// <c>MongoSelectLowerer</c> reads a computed sort key's <c>KeySelector.Type</c>, and a conditional can be
    /// a computed sort key.
    /// </remarks>
    public override Type Type { get; } = ifTrue is MongoConstantExpression { Value: null } ? ifFalse.Type : ifTrue.Type;
}
