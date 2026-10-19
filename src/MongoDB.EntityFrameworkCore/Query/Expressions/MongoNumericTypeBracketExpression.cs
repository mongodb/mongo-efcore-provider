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
/// Query-dialect <c>{ field: { $type: "number" } }</c>: the field is present and numeric, excluding missing, null,
/// and every other BSON type.
/// </summary>
/// <remarks>
/// Emitted by <see cref="MongoDB.EntityFrameworkCore.Query.NativeTranslation.MongoExpressionTranslator"/> as the left
/// <see cref="MongoBinaryOperator.AndAlso"/> conjunct beside a numeric-cast relational comparison that renders via
/// <c>$expr</c>. The query dialect type-brackets relational operators, but <c>$expr</c> treats missing as null and
/// sorts null below every number, so without this bracket <c>&lt;</c>/<c>&lt;=</c> would admit missing/null rows.
/// <c>{ $ne: null }</c> wouldn't exclude foreign types. Renders as <c>$isNumber</c> in the aggregation dialect.
/// The negator declines it; a negated bracketed comparison is <c>$not</c>-wrapped whole, its exact complement.
/// </remarks>
internal sealed class MongoNumericTypeBracketExpression(MongoFieldExpression field) : MongoExpression
{
    /// <summary>The field whose stored BSON type is tested.</summary>
    public new MongoFieldExpression Field { get; } = field;

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
