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
/// A ternary conditional (<c>test ? ifTrue : ifFalse</c>), rendered in the aggregation-expression dialect as
/// <c>$cond</c>.
/// </summary>
/// <remarks>
/// <see cref="Test"/> is always rendered by <c>MongoAggregationExpressionRenderer</c>, never the query dialect:
/// <c>$cond.if</c> is an expression context. Not admitted by <c>IsQueryDialectRenderable</c>, since it has no
/// query-dialect form and <c>$expr</c> is a server error inside <c>$elemMatch</c>.
/// </remarks>
internal sealed class MongoConditionalExpression(MongoExpression test, MongoExpression ifTrue, MongoExpression ifFalse)
    : MongoExpression
{
    /// <summary>The boolean condition.</summary>
    public MongoExpression Test { get; } = test;

    /// <summary>The value when <see cref="Test"/> is true.</summary>
    public MongoExpression IfTrue { get; } = ifTrue;

    /// <summary>The value when <see cref="Test"/> is false.</summary>
    public MongoExpression IfFalse { get; } = ifFalse;

    /// <inheritdoc />
    /// <remarks>
    /// Uses <see cref="IfFalse"/>'s type when <see cref="IfTrue"/> is a null constant (typed <c>object</c>);
    /// <c>MongoSelectLowerer</c> reads a computed sort key's type.
    /// </remarks>
    public override Type Type { get; } = ifTrue is MongoConstantExpression { Value: null } ? ifFalse.Type : ifTrue.Type;
}
