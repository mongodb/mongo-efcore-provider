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
/// <c>$ifNull</c>. <c>a ?? b ?? c</c> nests on the right: <c>Coalesce(a, Coalesce(b, c))</c>.
/// </summary>
/// <remarks>
/// Not query-dialect renderable (like <see cref="MongoConditionalExpression"/>): it would need <c>$expr</c>,
/// which is a server error inside <c>$elemMatch</c>.
/// </remarks>
internal sealed class MongoCoalesceExpression(MongoExpression left, MongoExpression right) : MongoExpression
{
    /// <summary>The primary operand, used when non-null.</summary>
    public MongoExpression Left { get; } = left;

    /// <summary>The fallback operand, used when <see cref="Left"/> is null.</summary>
    public MongoExpression Right { get; } = right;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="Right"/>'s type, matching C# <c>??</c> typing (<c>int? ?? int</c> is <c>int</c>), unless
    /// <see cref="Right"/> is a null constant (typed <c>object</c>), in which case <see cref="Left"/>'s type.
    /// </remarks>
    public override Type Type { get; } = right is MongoConstantExpression { Value: null } ? left.Type : right.Type;
}
