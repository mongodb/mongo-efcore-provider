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
/// Query-dialect <c>{ field: { $type: "number" } }</c> test that a stored field is present and holds a
/// numeric BSON value (<c>int</c>/<c>long</c>/<c>double</c>/<c>decimal128</c>) — excluding both a missing
/// element and an explicit BSON <c>null</c>, and (unlike <c>{ field: { $ne: null } }</c>) every other foreign
/// BSON type as well.
/// </summary>
/// <remarks>
/// <para>
/// Produced only by <see cref="MongoDB.EntityFrameworkCore.Query.NativeTranslation.MongoExpressionTranslator"/>
/// as the <c>Left</c> conjunct of an <see cref="MongoBinaryExpression"/>
/// (<see cref="MongoBinaryOperator.AndAlso"/>) whose <c>Right</c> is a relational comparison rendered through
/// <c>$expr</c> over a <see cref="MongoConvertExpression"/> — the fall-through a numeric-cast relational
/// comparison over a NULLABLE property takes. The query dialect itself type-brackets a relational operator
/// (<c>&lt; &lt;= &gt; &gt;=</c>): it matches neither a missing element nor an explicit <c>null</c>, and
/// admits only the same BSON "Numbers" comparison class this node tests for. <c>$expr</c>'s own comparison
/// does not: it converts both a missing element and <c>null</c> to <c>null</c>, and BSON total order sorts
/// <c>Null</c> below every number, so an un-bracketed <c>$expr</c> comparison would silently admit those rows
/// under <c>&lt;</c>/<c>&lt;=</c>. This node is what makes the combined <c>$and</c> the EXACT complement the
/// query dialect would have produced, rather than the approximation <c>{ field: { $ne: null } }</c> would be
/// (that form excludes a missing/null field but not a genuinely foreign BSON type such as a stray string).
/// </para>
/// <para>
/// Query-dialect only — there is no aggregation-expression form, because this node is never itself nested
/// inside an <c>$expr</c>; it always sits beside one as a top-level <c>$match</c> conjunct.
/// </para>
/// </remarks>
internal sealed class MongoNumericTypeBracketExpression(MongoFieldExpression field) : MongoExpression
{
    /// <summary>The field whose stored BSON type is tested.</summary>
    // 'new' hides the inherited Expression.Field(...) method; used for semantic clarity (matches
    // MongoArrayContainsExpression's own Field property).
    public new MongoFieldExpression Field { get; } = field;

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
