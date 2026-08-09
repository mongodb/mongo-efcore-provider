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
/// An explicit type conversion of <see cref="Operand"/> to <see cref="Type"/>, rendered in the
/// aggregation-expression dialect as one of MQL's four <c>$to…</c> operators.
/// </summary>
/// <remarks>
/// <para>
/// EF-322 stream 1, slice A1. Built for a cast the translator must NOT simply unwrap — a narrowing or
/// signed/unsigned conversion changes the value, so dropping it silently changes results (a sort key ordered
/// by the raw stored value; a comparison evaluated in the wrong type).
/// </para>
/// <para>
/// <b>The admissible set is bounded by MQL itself, not by taste.</b> <see cref="ToOperatorFor"/> maps only
/// <see cref="int"/>, <see cref="long"/>, <see cref="double"/> and <see cref="decimal"/>; there is no
/// <c>$toShort</c>, <c>$toUInt</c> or <c>$toFloat</c>. MEASURED: the driver's own LINQ provider throws
/// <c>ExpressionNotSupportedException</c> for those targets in predicate, sort and projection position alike,
/// so declining them keeps native and the fallback at the SAME boundary.
/// </para>
/// <para>
/// <b>This node is deliberately NOT admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.</b>
/// It has no query-dialect form, and <c>$expr</c> is a hard server error inside <c>$elemMatch</c> — so
/// admitting it there would turn a clean decline into a runtime failure. See that classifier's own remarks.
/// </para>
/// <para>
/// <b>Overflow/<c>ConvertChecked</c> semantics are UNVERIFIED, not silently assumed safe.</b>
/// <c>MongoExpressionTranslator</c>'s <c>TranslateOperand</c> maps both <c>Convert</c> and <c>ConvertChecked</c>
/// to this SAME node — there is no separate "checked" flavor — yet the two diverge for an out-of-range value:
/// unchecked C# produces an unspecified truncated/wrapped result, while MQL's <c>$toInt</c>/<c>$toLong</c>/
/// <c>$toDecimal</c> raise a server error on a value that does not fit the target type. UNVERIFIED whether the
/// driver's own LINQ provider emits the identical <c>$toX</c> for this exact overflow case (it does for the
/// in-range shapes this task measured, per the spike's §3.1 table) — if it does, this joins the EF-359
/// "accept and document" family (native and the driver agree with each other and both differ from unchecked
/// C#, which is the divergence to accept); if it does not, the divergence needs its own decision. Neither
/// direction has been measured here.
/// </para>
/// </remarks>
internal sealed class MongoConvertExpression(MongoExpression operand, Type clrType) : MongoExpression
{
    /// <summary>The expression whose value is converted.</summary>
    public MongoExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override Type Type { get; } = clrType;

    /// <summary>
    /// The MQL conversion operator for <paramref name="clrType"/>, or <see langword="null"/> when MQL cannot
    /// express it. This is the single definition of the admissible set — every gate consults it rather than
    /// re-deriving one.
    /// </summary>
    public static string? ToOperatorFor(Type clrType)
    {
        var target = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return target == typeof(int) ? "$toInt"
            : target == typeof(long) ? "$toLong"
            : target == typeof(double) ? "$toDouble"
            : target == typeof(decimal) ? "$toDecimal"
            : null;
    }
}
