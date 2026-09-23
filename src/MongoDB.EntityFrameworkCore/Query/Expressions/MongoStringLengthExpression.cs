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
/// The <c>string.Length</c> member access — the number of UTF-16 code units in <see cref="Operand"/> — rendered
/// in the aggregation-expression dialect as <c>$strLenCP</c>.
/// </summary>
/// <remarks>
/// <see cref="Operand"/> is the general <see cref="MongoExpression"/> type, not a bare field — <c>$strLenCP</c>
/// accepts any string-valued EXPRESSION, matching <see cref="MongoStringIndexOfExpression"/>'s own operand
/// shape. Like <see cref="MongoStringIndexOfExpression"/>, this node has no query-dialect form (it produces an
/// integer VALUE, not a predicate) and must never be admitted by
/// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.
/// </remarks>
internal sealed class MongoStringLengthExpression(MongoExpression operand) : MongoExpression
{
    /// <summary>The string-valued expression whose length is computed.</summary>
    public MongoExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(int);
}
