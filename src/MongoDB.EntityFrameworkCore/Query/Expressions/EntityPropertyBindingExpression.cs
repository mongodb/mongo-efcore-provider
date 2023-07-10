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
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Indicates a property that will be bound to the underlying BSON
/// element result.
/// </summary>
internal sealed class EntityPropertyBindingExpression : Expression
{
    /// <summary>
    /// Create a <see cref="EntityPropertyBindingExpression"/>.
    /// </summary>
    /// <param name="boundProperty">The <see cref="IProperty"/> this expression was bound to.</param>
    public EntityPropertyBindingExpression(IProperty boundProperty)
    {
        BoundProperty = boundProperty;
    }

    /// <summary>
    /// The <see cref="IProperty"/> this expression is bound to.
    /// </summary>
    public IProperty BoundProperty { get; }

    /// <inheritdoc/>
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc/>
    public override Type Type
        => BoundProperty.ClrType;

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is EntityPropertyBindingExpression propertyBindingExpression
               && Equals(propertyBindingExpression));

    private bool Equals(EntityPropertyBindingExpression propertyBindingExpression)
        => Equals(BoundProperty, propertyBindingExpression.BoundProperty);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(BoundProperty);
}
