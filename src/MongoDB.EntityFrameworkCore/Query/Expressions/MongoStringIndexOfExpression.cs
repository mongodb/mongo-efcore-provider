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
/// The single-argument, ordinal <c>string.IndexOf(string)</c> overload — the character index of
/// <see cref="Needle"/>'s first occurrence in <see cref="Haystack"/>, or -1 if absent — rendered in the
/// aggregation-expression dialect as <c>$indexOfCP</c>.
/// </summary>
/// <remarks>
/// Both operands are the general <see cref="MongoExpression"/> type, not a bare field — <c>$indexOfCP</c>
/// accepts any string-valued EXPRESSION for either argument, matching <see cref="MongoDateAddExpression"/>'s
/// own operand shape. Like <see cref="MongoDatePartExpression"/> and <see cref="MongoDateAddExpression"/>,
/// this node has no query-dialect form (it produces an integer VALUE, not a predicate) and must never be
/// admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.
/// </remarks>
internal sealed class MongoStringIndexOfExpression(MongoExpression haystack, MongoExpression needle) : MongoExpression
{
    /// <summary>The string-valued expression searched.</summary>
    public MongoExpression Haystack { get; } = haystack;

    /// <summary>The string-valued expression searched for.</summary>
    public MongoExpression Needle { get; } = needle;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(int);
}
