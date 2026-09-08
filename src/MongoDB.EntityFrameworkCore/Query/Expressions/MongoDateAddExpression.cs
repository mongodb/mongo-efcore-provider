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
/// The date-arithmetic units this provider translates natively — exactly the <see cref="DateTime"/>/
/// <see cref="DateTimeOffset"/> <c>AddXxx</c> overloads that map 1:1 onto a <c>$dateAdd</c> <c>unit</c> string.
/// <c>AddTicks</c> and <c>Add(TimeSpan)</c> are deliberately absent — <c>$dateAdd</c> has no <c>tick</c> unit,
/// and a decline falls back gracefully to the existing driver-LINQ bridge.
/// </summary>
internal enum MongoDateAddUnit
{
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
    Millisecond
}

/// <summary>
/// Adds a signed <see cref="Amount"/> of <see cref="Unit"/>s to a datetime-valued <see cref="StartDate"/>,
/// rendered in the aggregation-expression dialect as <c>$dateAdd</c>.
/// </summary>
/// <remarks>
/// Both <see cref="StartDate"/> and <see cref="Amount"/> are the general <see cref="MongoExpression"/> type,
/// not a bare field — <c>$dateAdd</c> accepts any date-valued/numeric-valued EXPRESSION for either argument,
/// which is what lets this node wrap a field, a constant, or another computed expression (including a nested
/// <see cref="MongoDateAddExpression"/>, for a chained <c>.AddDays(...).AddMinutes(...)</c>).
/// <para>
/// Like <see cref="MongoDatePartExpression"/>, this node has no query-dialect form and must never be admitted
/// by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.
/// </para>
/// </remarks>
internal sealed class MongoDateAddExpression(MongoExpression startDate, MongoDateAddUnit unit, MongoExpression amount)
    : MongoExpression
{
    /// <summary>The datetime-valued expression to add to.</summary>
    public MongoExpression StartDate { get; } = startDate;

    /// <summary>Which unit <see cref="Amount"/> is expressed in.</summary>
    public MongoDateAddUnit Unit { get; } = unit;

    /// <summary>The (possibly negative) signed amount of <see cref="Unit"/>s to add.</summary>
    public MongoExpression Amount { get; } = amount;

    /// <inheritdoc />
    public override Type Type { get; } = startDate.Type;
}
