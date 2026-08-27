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
/// The date-part components this provider translates natively. <c>TimeOfDay</c> is deliberately absent — MQL
/// has no single clean composition for it, and a decline falls back gracefully to the existing driver-LINQ
/// bridge (<c>MongoEFToLinqTranslatingExpressionVisitor</c>), which already handles it.
/// </summary>
internal enum MongoDatePart
{
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
    Millisecond,
    DayOfWeek,
    DayOfYear,
    Date
}

/// <summary>
/// Extracts one <see cref="MongoDatePart"/> component from a datetime-valued <see cref="Operand"/>, rendered
/// in the aggregation-expression dialect as the matching MQL date operator (<c>$year</c>, <c>$month</c>,
/// <c>$dayOfMonth</c>, <c>$hour</c>, <c>$minute</c>, <c>$second</c>, <c>$millisecond</c>, <c>$dayOfWeek</c>,
/// <c>$dayOfYear</c>, or <c>$dateTrunc</c> for <see cref="MongoDatePart.Date"/>).
/// </summary>
/// <remarks>
/// <see cref="Operand"/> is deliberately typed as the general <see cref="MongoExpression"/>, not a bare field:
/// every one of MQL's date-extraction operators accepts any date-valued EXPRESSION, not just a field
/// reference, which is what lets this node wrap a <see cref="MongoDateTimeOffsetLocalExpression"/> (the
/// reconstructed local time for a <c>DateTimeOffset</c> source) as well as a plain <c>DateTime</c> field.
/// <para>
/// Like <see cref="MongoConvertExpression"/>, this node has no query-dialect form and must never be admitted
/// by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.
/// </para>
/// </remarks>
internal sealed class MongoDatePartExpression(MongoExpression operand, MongoDatePart part) : MongoExpression
{
    /// <summary>The datetime-valued expression to extract a component from.</summary>
    public MongoExpression Operand { get; } = operand;

    /// <summary>Which component to extract.</summary>
    public MongoDatePart Part { get; } = part;

    /// <inheritdoc />
    public override Type Type { get; } = part switch
    {
        MongoDatePart.Date => typeof(DateTime),
        MongoDatePart.DayOfWeek => typeof(DayOfWeek),
        _ => typeof(int)
    };
}
