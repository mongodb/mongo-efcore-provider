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
/// The date-part components translated natively.
/// </summary>
/// <remarks>
/// <see cref="TimeOfDay"/> is produced only as a whole projection leaf over a plain <c>DateTime</c> field
/// (<c>MongoExpressionTranslator.TryTranslateTimeOfDayLeaf</c>), never by the general value translator: it renders
/// as milliseconds since midnight, so only a read through
/// <c>BsonSerializerFactory.TimeOfDayMillisecondsSerializer</c> gives the right <see cref="TimeSpan"/>, and a filter,
/// group key or accumulator would compare or read those milliseconds as a <see cref="TimeSpan"/> in its own
/// representation.
/// </remarks>
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
    Date,
    TimeOfDay
}

/// <summary>
/// Extracts one <see cref="MongoDatePart"/> from a datetime-valued <see cref="Operand"/> via the matching MQL date
/// operator (<c>$year</c>, <c>$month</c>, …, <c>$dateTrunc</c> for <see cref="MongoDatePart.Date"/>, or the driver's
/// <c>$dateDiff</c> from the day-truncated date in milliseconds for <see cref="MongoDatePart.TimeOfDay"/>).
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

    /// <summary>
    /// Whether <paramref name="node"/> is a <see cref="MongoDatePart.TimeOfDay"/> leaf: milliseconds, not a
    /// <see cref="TimeSpan"/>'s default stored form. The one predicate the emit side's declines
    /// (<c>AllFieldsDefaultSerialized</c>, the projected Distinct, set-op <c>OperandSerializationsMatch</c>) and the read side
    /// (<c>MongoSelectDefinition.IsTimeOfDayProjection</c>) share.
    /// </summary>
    internal static bool IsTimeOfDay(MongoExpression node)
        => node is MongoDatePartExpression { Part: MongoDatePart.TimeOfDay };

    /// <inheritdoc />
    public override Type Type { get; } = part switch
    {
        MongoDatePart.Date => typeof(DateTime),
        MongoDatePart.DayOfWeek => typeof(DayOfWeek),
        MongoDatePart.TimeOfDay => typeof(TimeSpan),
        _ => typeof(int)
    };
}
