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
/// A field reference that always resolves at the document root, even inside a <c>$filter</c>/<c>$map</c> scope
/// that has bound its element to a variable.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="MongoFieldExpression"/> rather than a flag on it (same reasoning as
/// <see cref="MongoFilteredSizeExpression"/>). Used by a two-scope
/// <see cref="NativeTranslation.MongoExpressionTranslator"/> for members rooted on the outer parameter. Outside a
/// <c>$filter</c>/<c>$map</c> it renders like an ordinary field; inside one, an ordinary field would render as
/// <c>"$$" + elementVariable + "." + path</c> (the element), whereas this keeps <c>"$" + path</c>.
/// </para>
/// <para>
/// Aggregation-expression only; <see cref="NativeTranslation.MongoQueryLanguageRenderer.IsQueryDialectRenderable"/>
/// declines it unconditionally.
/// </para>
/// </remarks>
internal sealed class MongoOuterFieldExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoOuterFieldExpression"/> for the given property.
    /// </summary>
    /// <param name="property">The EF Core <see cref="IProperty"/> this field corresponds to.</param>
    /// <param name="elementName">The document element name, relative to the outer document root.</param>
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
