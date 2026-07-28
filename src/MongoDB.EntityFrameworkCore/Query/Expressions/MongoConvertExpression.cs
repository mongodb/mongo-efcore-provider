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
/// <b>Overflow/<c>ConvertChecked</c> semantics are MEASURED (EF-403 fix wave), and the answer is worse than
/// the earlier UNVERIFIED tag assumed: ONE out-of-range document ABORTS THE ENTIRE QUERY.</b>
/// <c>MongoExpressionTranslator</c>'s <c>TranslateOperand</c> maps both <c>Convert</c> and <c>ConvertChecked</c>
/// to this SAME node — there is no separate "checked" flavor. Measured against a live server over two
/// documents, <c>{D: 1.6}</c> and <c>{D: 1e30}</c>:
/// </para>
/// <para>
/// <c>{$expr: {$gt: [{$toInt: "$D"}, 0]}}</c> → <c>MongoCommandException: Conversion would overflow target
/// type in $convert with no onError value: 1e+30</c>; <c>{D: {$gt: 0}}</c> — what the released
/// <c>v10.0.2</c>/<c>v9.1.2</c>/<c>v8.4.2</c> packages emit for the same LINQ, the cast dropped — returned BOTH
/// rows.
/// </para>
/// <para>
/// <b>BLAST RADIUS, which is the part the UNVERIFIED tag obscured: the failure is PER-QUERY, not per-row.</b>
/// <c>$expr</c> is evaluated for every document the stage SCANS, so a single unconvertible value aborts the
/// whole aggregate — including for documents that would never have matched the predicate, and including
/// documents that were never going to be returned. It is not "the offending row errors"; it is "no rows at
/// all, for anyone".
/// </para>
/// <para>
/// <b>DISPOSITION, re-taken explicitly on that measurement rather than inherited: keep the server error. Do
/// NOT add <c>$convert</c>'s <c>onError</c>.</b> Three answers are available and all three differ — released
/// behaviour returns rows (the cast silently dropped, so it answers a different question); unchecked C#
/// produces an unspecified wrapped value; <c>onError: null</c> would produce a THIRD answer, matching neither,
/// because a converted-to-<c>null</c> operand then participates in a BSON-total-order comparison
/// (<c>Null</c> below every number) and quietly moves the row into or out of the result depending on the
/// operator — reintroducing exactly the silent, operator-dependent behaviour the relational/nullable guard in
/// <c>MongoExpressionTranslator.CanFallThroughToExpr</c> exists to prevent. A loud abort is the only one of
/// the three that cannot be mistaken for an answer, and out-of-range data reaching a narrowing cast is a
/// genuine defect in the query or the data. Recorded in <c>BREAKING-CHANGES.md</c> as an observable delta from
/// the released packages, with <c>UseQueryMode(MongoQueryMode.DriverLinq)</c> as the mitigation.
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
