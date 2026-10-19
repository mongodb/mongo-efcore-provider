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
/// String concatenation (<c>a + b</c> with a <see cref="string"/> result), rendered as <c>$concat</c> over
/// <see cref="Operands"/>. Non-string operands arrive wrapped in a <see cref="MongoConvertExpression"/> to
/// <see cref="string"/>; see <c>MongoExpressionTranslator.TranslateStringConcat</c>.
/// </summary>
/// <remarks>
/// <c>$expr</c>-only: it has no query-dialect form, and <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>
/// rejects it via its catch-all.
/// </remarks>
internal sealed class MongoConcatExpression(IReadOnlyList<MongoExpression> operands) : MongoExpression
{
    /// <summary>The pieces being concatenated, in order.</summary>
    public IReadOnlyList<MongoExpression> Operands { get; } = operands;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(string);
}
