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
/// Represents an array-field-contains-value test — <c>arrayField.Contains(constant)</c> — the MIRROR shape
/// of <see cref="MongoInExpression"/> (a collection of values containing a field). Here the FIELD is the
/// stored array and <see cref="Value"/> is the single candidate value.
/// </summary>
/// <remarks>
/// Deliberately a SEALED SIBLING type rather than reuse of <see cref="MongoBinaryExpression"/>'s
/// <c>Equal</c> operator: <c>MongoAggregationExpressionRenderer.RenderBinary</c> maps <c>Equal</c> to the
/// aggregation-dialect <c>$eq</c> unconditionally, which for an ARRAY field tests whole-array equality, not
/// array-element membership — reusing <c>Equal</c> would silently answer wrong the moment this shape is used
/// as a VALUE (e.g. a computed sort key or projection leaf) and routed through the aggregation-expression
/// renderer instead of the query-dialect renderer. Keeping it a distinct node type means the aggregation
/// renderer's catch-all correctly REFUSES it (see
/// <c>MongoAggregationExpressionRenderer.CanRender</c>/<c>Render</c>), forcing a graceful decline instead of a
/// silent-wrong-data render, exactly the "sealed sibling type, not a bool flag" pattern this codebase already
/// uses for <see cref="MongoFilteredSizeExpression"/> next to <see cref="MongoSizeExpression"/>.
/// </remarks>
internal sealed class MongoArrayContainsExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoArrayContainsExpression"/>.
    /// </summary>
    /// <param name="field">The stored array field being tested.</param>
    /// <param name="value">The single candidate value to test for array membership.</param>
    /// <param name="negated"><see langword="true"/> for negated membership (the value is not an element).</param>
    public MongoArrayContainsExpression(MongoFieldExpression field, MongoExpression value, bool negated)
    {
        Field = field;
        Value = value;
        Negated = negated;
    }

    /// <summary>The stored array field being tested.</summary>
    // 'new' hides the inherited Expression.Field(...) method; used for semantic clarity.
    public new MongoFieldExpression Field { get; }

    /// <summary>The single candidate value to test for array membership.</summary>
    public MongoExpression Value { get; }

    /// <summary><see langword="true"/> for negated membership (the value is not an element of the array).</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
