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
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Indicates a binding to a BSON element in the returned
/// BSON document with a specific type.
/// </summary>
internal sealed class BsonElementBindingExpression : Expression
{
    /// <summary>
    /// Create a <see cref="BsonElementBindingExpression"/>.
    /// </summary>
    /// <param name="elementName">Name of the element within the BSON document.</param>
    /// <param name="bsonType">The type of data to be deserialized from the element.</param>
    public BsonElementBindingExpression(string elementName, Type bsonType)
    {
        ElementName = elementName;
        Type = bsonType;
    }

    /// <summary>
    /// The name of the BSON element this expression is bound to.
    /// </summary>
    public string ElementName { get; }

    /// <inheritdoc/>
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc/>
    public override Type Type { get; }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is BsonElementBindingExpression bsonElementBindingExpression
               && Equals(bsonElementBindingExpression));

    private bool Equals(BsonElementBindingExpression bsonElementBindingExpression)
        => Equals(ElementName, bsonElementBindingExpression.ElementName) &&
           Type == bsonElementBindingExpression.Type;

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(ElementName, Type);
}
