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
/// An array-field-contains-value test (<c>arrayField.Contains(value)</c>) — the mirror of
/// <see cref="MongoInExpression"/>.
/// </summary>
/// <remarks>
/// A sealed sibling rather than <see cref="MongoBinaryExpression"/> <c>Equal</c>: the aggregation renderer
/// maps <c>Equal</c> to <c>$eq</c>, which tests whole-array equality, so reusing it would answer wrong as a
/// value (sort key, projection leaf). As a distinct type, <c>MongoAggregationExpressionRenderer.CanRender</c>
/// refuses it and the query declines instead.
/// </remarks>
internal sealed class MongoArrayContainsExpression : MongoExpression
{
    /// <summary>Creates a <see cref="MongoArrayContainsExpression"/>.</summary>
    /// <param name="field">The stored array field.</param>
    /// <param name="value">The candidate element.</param>
    /// <param name="negated"><see langword="true"/> for "not an element".</param>
    public MongoArrayContainsExpression(MongoFieldExpression field, MongoExpression value, bool negated)
    {
        Field = field;
        Value = value;
        Negated = negated;
    }

    /// <summary>The stored array field being tested.</summary>
    public new MongoFieldExpression Field { get; }

    /// <summary>The single candidate value to test for array membership.</summary>
    public MongoExpression Value { get; }

    /// <summary><see langword="true"/> for "not an element".</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
