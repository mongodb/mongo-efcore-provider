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
/// Represents a reference to a named document field, identified by its
/// <see cref="IProperty"/> metadata and serialized element name.
/// </summary>
internal sealed class MongoFieldExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoFieldExpression"/> for the given property.
    /// </summary>
    /// <param name="property">The EF Core <see cref="IProperty"/> this field corresponds to.</param>
    /// <param name="elementName">The document element name used in the document.</param>
    public MongoFieldExpression(IProperty property, string elementName)
    {
        Property = property;
        ElementName = elementName;
    }

    /// <summary>The EF Core property metadata for this field.</summary>
    // 'new' hides the inherited static Expression.Property(...) factory; the member name is spec-mandated.
    public new IProperty Property { get; }

    /// <summary>The document element name for this field in the document.</summary>
    public string ElementName { get; }

    /// <inheritdoc />
    public override Type Type
        => Property.ClrType;
}
