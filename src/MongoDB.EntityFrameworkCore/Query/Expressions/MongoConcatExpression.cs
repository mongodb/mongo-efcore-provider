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
using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// C#'s compiler-generated string concatenation (<c>a + b</c> where the result type is <see cref="string"/>),
/// rendered as MQL's <c>$concat</c> over <see cref="Operands"/>. A non-string operand arrives wrapped in a
/// <see cref="MongoConvertExpression"/> targeting <see cref="string"/> (<c>$toString</c>) — see
/// <c>MongoExpressionTranslator.TranslateStringConcat</c> for how operands are produced and flattened.
/// </summary>
/// <remarks>
/// This node has no query-dialect form — like <see cref="MongoConvertExpression"/>, it is <c>$expr</c>-only
/// and must stay excluded from <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c> (the catch-all
/// already returns <see langword="false"/> for an unrecognized node kind, so no explicit arm is needed there).
/// </remarks>
internal sealed class MongoConcatExpression(IReadOnlyList<MongoExpression> operands) : MongoExpression
{
    /// <summary>The pieces being concatenated, in order.</summary>
    public IReadOnlyList<MongoExpression> Operands { get; } = operands;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(string);
}
