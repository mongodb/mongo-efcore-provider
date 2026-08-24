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
/// The translator must NOT simply unwrap a cast — a narrowing or signed/unsigned conversion changes the
/// value, so dropping it silently changes results (a sort key ordered by the raw stored value; a comparison
/// evaluated in the wrong type).
/// </para>
/// <para>
/// <b>The admissible set is bounded by MQL itself, not by taste.</b> <see cref="ToOperatorFor"/> maps only
/// <see cref="int"/>, <see cref="long"/>, <see cref="double"/> and <see cref="decimal"/>; there is no
/// <c>$toShort</c>, <c>$toUInt</c> or <c>$toFloat</c>. The driver's own LINQ provider throws
/// <c>ExpressionNotSupportedException</c> for those targets in predicate, sort and projection position alike,
/// so declining them keeps native and the fallback at the SAME boundary.
/// </para>
/// <para>
/// <b>This node is deliberately NOT admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.</b>
/// It has no query-dialect form, and <c>$expr</c> is a hard server error inside <c>$elemMatch</c> — so
/// admitting it there would turn a clean decline into a runtime failure. See that classifier's own remarks.
/// </para>
/// <para>
/// <b><c>Convert</c> and <c>ConvertChecked</c> both map to this same node — there is no separate "checked"
/// flavor — and an out-of-range conversion aborts the whole query, not just the offending row.</b>
/// <c>$expr</c> is evaluated for every document the stage scans, so a single unconvertible value (e.g. a
/// double too large for <c>$toInt</c>) fails the aggregate for every document, including ones that would
/// never have matched the predicate or been returned.
/// </para>
/// <para>
/// <b>Disposition: keep the server error. Do NOT add <c>$convert</c>'s <c>onError</c>.</b> The released
/// (driver-LINQ) packages instead silently drop the cast, so out-of-range rows are returned uncast rather
/// than erroring — this is a deliberate, documented behavioral delta (see <c>BREAKING-CHANGES.md</c>), not an
/// oversight. An <c>onError: null</c> fallback was considered and rejected: a converted-to-<c>null</c> operand
/// participates in BSON's total-ordering comparisons (<c>Null</c> sorts below every number) and would quietly
/// move the row into or out of the result depending on the operator — reintroducing exactly the
/// silent, operator-dependent behavior the relational/nullable guard in
/// <c>MongoExpressionTranslator.CanFallThroughToExpr</c> exists to prevent. A loud abort is the only option
/// that can't be mistaken for a valid answer, and out-of-range data reaching a narrowing cast is a genuine
/// defect in the query or the data. <c>UseQueryMode(MongoQueryMode.DriverLinq)</c> is the mitigation for
/// anyone who needs the old silent-drop behavior.
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
