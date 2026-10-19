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
/// The date-part components translated natively. <c>TimeOfDay</c> is absent: MQL has no clean composition for
/// it, and the driver-LINQ fallback handles it.
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
/// Extracts one <see cref="MongoDatePart"/> from a datetime-valued <see cref="Operand"/> via the matching MQL date
/// operator (<c>$year</c>, <c>$month</c>, …, or <c>$dateTrunc</c> for <see cref="MongoDatePart.Date"/>).
/// </summary>
/// <remarks>
/// <see cref="Operand"/> is any date-valued expression, so it can wrap a
/// <see cref="MongoDateTimeOffsetLocalExpression"/> as well as a <c>DateTime</c> field. Aggregation dialect only;
/// must never be admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.
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
