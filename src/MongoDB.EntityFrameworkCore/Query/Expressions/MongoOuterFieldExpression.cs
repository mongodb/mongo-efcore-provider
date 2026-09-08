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
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// A field reference that always resolves at DOCUMENT ROOT, even when rendered inside a <c>$filter</c>/<c>$map</c>
/// scope that has bound its own element to a variable (see
/// <see cref="NativeTranslation.MongoAggregationExpressionRenderer"/>'s <c>elementVariable</c> parameter).
/// </summary>
/// <remarks>
/// <para>
/// A deliberate SIBLING of <see cref="MongoFieldExpression"/> rather than a flag on it (this codebase's own
/// convention for a node kind needing different handling at multiple existing sites — see
/// <see cref="MongoFilteredSizeExpression"/>'s remarks for the same reasoning). Used by a TWO-SCOPE
/// <see cref="NativeTranslation.MongoExpressionTranslator"/> to resolve a member rooted on its OUTER
/// parameter: outside any <c>$filter</c>/<c>$map</c> (e.g. a correlated <c>SelectMany</c> inner filter,
/// rendered as a top-level <c>$expr</c>) this renders identically to an ordinary <see cref="MongoFieldExpression"/>
/// at the same path — <c>elementVariable</c> is <see langword="null"/> there too. INSIDE a
/// <see cref="MongoFilteredSizeExpression"/>'s <c>$filter</c> or a quantifier's <c>$map</c>, however, an
/// ordinary <see cref="MongoFieldExpression"/> would be misread as "the filter's own element" (rendered
/// <c>"$$" + elementVariable + "." + path</c>) rather than the enclosing document this field actually belongs
/// to — this node exists specifically to keep rendering as <c>"$" + path</c> in that position too.
/// </para>
/// <para>
/// Aggregation-expression-ONLY: it has no query-dialect (<c>$match</c>) form at all, since it only has
/// meaning as an operand inside an aggregation expression that itself distinguishes an inner vs. outer scope.
/// <see cref="NativeTranslation.MongoQueryLanguageRenderer.IsQueryDialectRenderable"/> declines it
/// unconditionally.
/// </para>
/// </remarks>
internal sealed class MongoOuterFieldExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoOuterFieldExpression"/> for the given property.
    /// </summary>
    /// <param name="property">The EF Core <see cref="IProperty"/> this field corresponds to.</param>
    /// <param name="elementName">The document element name, relative to the OUTER (enclosing) document root.</param>
    public MongoOuterFieldExpression(IProperty property, string elementName)
    {
        Property = property;
        ElementName = elementName;
    }

    /// <summary>The EF Core property metadata for this field.</summary>
    public new IProperty Property { get; }

    /// <summary>The document element name, relative to the outer document root.</summary>
    public string ElementName { get; }

    /// <inheritdoc />
    public override Type Type => Property.ClrType;
}
