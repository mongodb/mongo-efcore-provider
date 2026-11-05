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

/// <summary>Which end of <see cref="MongoStringFirstOrLastExpression.Source"/> is extracted.</summary>
internal enum MongoStringFirstOrLastKind
{
    /// <summary><c>string.FirstOrDefault()</c> — the first character, or <c>'\0'</c> when empty.</summary>
    First,

    /// <summary><c>string.LastOrDefault()</c> — the last character, or <c>'\0'</c> when empty.</summary>
    Last
}

/// <summary>
/// <c>string.FirstOrDefault()</c>/<c>LastOrDefault()</c> — the first/last <c>char</c> of
/// <see cref="Source"/>, or <c>'\0'</c> when <see cref="Source"/> is empty.
/// </summary>
/// <remarks>
/// A dedicated node because composing <see cref="MongoConditionalExpression"/> over a raw <c>$substrCP</c> would give
/// the branches disagreeing static types. Renders as <c>$cond</c> on <c>$strLenCP(Source) == 0</c> so the
/// <c>$substrCP</c> never sees a negative start (<c>Last</c> over <c>""</c> would be a hard server error). Both
/// branches render as a one-character BSON string (the empty case as <c>"\0"</c>, not Int32 <c>0</c>), or a comparison
/// against <c>'\0'</c> would cross BSON type brackets and answer false. Value-only: must never be admitted by
/// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.
/// </remarks>
internal sealed class MongoStringFirstOrLastExpression(MongoExpression source, MongoStringFirstOrLastKind kind)
    : MongoExpression
{
    public MongoExpression Source { get; } = source;

    public MongoStringFirstOrLastKind Kind { get; } = kind;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(char);
}
