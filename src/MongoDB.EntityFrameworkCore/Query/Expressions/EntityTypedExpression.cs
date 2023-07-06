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
/// A base class for any type of <see cref="Expression"/> that is a
/// <see cref="ExpressionType.Extension"/> that exposes a
/// <see cref="IEntityType"/>.
/// </summary>
internal abstract class EntityTypedExpression : Expression
{
    /// <summary>
    /// Create a <see cref="EntityTypedExpression"/>.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> for this expression.</param>
    protected EntityTypedExpression(IEntityType entityType)
    {
        EntityType = entityType;
    }

    /// <summary>
    /// The <see cref="IEntityType"/> this expression relates to.
    /// </summary>
    public IEntityType EntityType { get; }

    /// <inheritdoc/>
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc/>
    public override Type Type
        => EntityType.ClrType;

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is EntityTypedExpression entityTypedExpression
               && Equals(entityTypedExpression));

    private bool Equals(EntityTypedExpression entityTypedExpression)
        => Equals(EntityType, entityTypedExpression.EntityType);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(EntityType);
}
