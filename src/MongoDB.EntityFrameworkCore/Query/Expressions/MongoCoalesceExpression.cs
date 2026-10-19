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
/// A null-coalescing operator (<c>left ?? right</c>), rendered in the aggregation-expression dialect as
/// <c>$ifNull</c>. A chained coalesce (<c>a ?? b ?? c</c>) nests on the RIGHT operand, matching
/// <see cref="System.Linq.Expressions.BinaryExpression"/>'s own left-associative <c>Coalesce</c> shape, so
/// <c>a ?? b ?? c</c> becomes <c>MongoCoalesceExpression(a, MongoCoalesceExpression(b, c))</c>.
/// </summary>
/// <remarks>
/// This node is deliberately NOT admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c> — same
/// as its sibling <see cref="MongoConditionalExpression"/> — it has no query-dialect form, and <c>$expr</c>
/// (which is what a native <c>$project</c>/<c>$match</c> would need to wrap it in) is a hard server error inside
/// <c>$elemMatch</c>.
/// </remarks>
internal sealed class MongoCoalesceExpression(MongoExpression left, MongoExpression right) : MongoExpression
{
    /// <summary>The primary operand, used when non-null.</summary>
    public MongoExpression Left { get; } = left;

    /// <summary>The fallback operand, used when <see cref="Left"/> is null.</summary>
    public MongoExpression Right { get; } = right;

    /// <inheritdoc />
    /// <remarks>
    /// Prefers <see cref="Right"/>'s type — matching C#'s own <c>??</c> typing (<c>int? ?? int</c> has type
    /// <c>int</c>, the non-nullable right operand's type) — unless <see cref="Right"/> is a null-valued
    /// <see cref="MongoConstantExpression"/>, whose own <c>.Type</c> falls back to <c>typeof(object)</c> and
    /// carries no meaningful type information; then <see cref="Left"/>'s type is used instead. Same fallback
    /// shape <see cref="MongoConditionalExpression"/> uses for its own branch preference.
    /// </remarks>
    public override Type Type { get; } = right is MongoConstantExpression { Value: null } ? left.Type : right.Type;
}
