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
/// Which end of <see cref="MongoStringFirstOrLastExpression.Source"/> is extracted.
/// </summary>
internal enum MongoStringFirstOrLastKind
{
    /// <summary><c>string.FirstOrDefault()</c> — the first character, or <c>'\0'</c> when empty.</summary>
    First,

    /// <summary><c>string.LastOrDefault()</c> — the last character, or <c>'\0'</c> when empty.</summary>
    Last
}

/// <summary>
/// <c>string.FirstOrDefault()</c>/<c>LastOrDefault()</c> — the first/last <c>char</c> of
/// <see cref="Source"/>, or <c>default(char)</c> (<c>'\0'</c>) when <see cref="Source"/> is the empty string.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated node, not a composition of <see cref="MongoConditionalExpression"/> +
/// <see cref="MongoStringLengthExpression"/> + a raw <c>$substrCP</c> operand, because there is no existing
/// node that renders a bare <c>$substrCP</c> — every other string node in this family
/// (<see cref="MongoStringIndexOfExpression"/>, <see cref="MongoStringLengthExpression"/>,
/// <see cref="MongoTrimExpression"/>) already has a 1:1 MQL operator to render as. This one instead composes
/// the empty-safe extraction (<c>$cond</c> guarding a <c>$substrCP</c> with an in-range start) as a single unit,
/// matching <see cref="MongoTrimExpression"/>'s own precedent — a small dedicated node rather than trying to
/// force the composition through existing nodes whose static <see cref="Type"/> would disagree across the
/// conditional's branches (an int literal for the empty case vs. a string from <c>$substrCP</c> for the
/// non-empty case; MQL has no <c>$toChar</c> to reconcile them, unlike <see cref="MongoConvertExpression"/>'s
/// bounded <c>int</c>/<c>long</c>/<c>double</c>/<c>decimal</c>/<c>string</c> set).
/// </para>
/// <para>
/// <b>Empty-string contract, confirmed empirically against a real server (EF-322 Task 4).</b> .NET's
/// <c>FirstOrDefault</c>/<c>LastOrDefault</c> never throw — an empty source yields <c>default(char)</c>
/// (<c>'\0'</c>), never an exception. MongoDB's <c>$substrCP</c> answers <c>""</c> (not an error) for an
/// out-of-range but NON-NEGATIVE start (including a start equal to the string's own length, which is what
/// <c>Last</c> over an empty string would otherwise compute: <c>$strLenCP - 1 = -1</c>) — but a NEGATIVE
/// start is a hard server error (<c>"the starting index must be nonnegative integer"</c>), so <c>Last</c>
/// cannot simply subtract 1 unconditionally. The rendering therefore gates BOTH forms on
/// <c>$strLenCP(Source) == 0</c> up front, via <c>$cond</c>, and never lets the <c>else</c> branch's
/// <c>$substrCP</c> see a negative start — confirmed empirically that <c>$cond</c> short-circuits (the
/// untaken branch, even one that would itself be a hard error if evaluated, is never evaluated).
/// </para>
/// <para>
/// The <c>then</c> (empty) branch renders as the BSON <c>Int32</c> literal <c>0</c>, not a one-character
/// string — <c>CharSerializer</c>'s <c>Deserialize</c> switches on the ACTUAL wire <see cref="Type"/> it
/// reads (<c>Int32</c>/<c>Int64</c>/<c>String</c> are all accepted, independent of the serializer's own
/// configured representation), so a computed <c>char</c> leaf may freely mix an <c>Int32</c> zero for the
/// empty case with a one-character <c>String</c> for the non-empty case.
/// </para>
/// <para>
/// Like <see cref="MongoStringIndexOfExpression"/>/<see cref="MongoStringLengthExpression"/>/
/// <see cref="MongoTrimExpression"/>, this node has no query-dialect form (it produces a VALUE, not a
/// predicate) and must never be admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.
/// </para>
/// </remarks>
internal sealed class MongoStringFirstOrLastExpression(MongoExpression source, MongoStringFirstOrLastKind kind)
    : MongoExpression
{
    /// <summary>The string-valued expression whose first/last character is extracted.</summary>
    public MongoExpression Source { get; } = source;

    /// <summary>Which end of <see cref="Source"/> is extracted.</summary>
    public MongoStringFirstOrLastKind Kind { get; } = kind;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(char);
}
