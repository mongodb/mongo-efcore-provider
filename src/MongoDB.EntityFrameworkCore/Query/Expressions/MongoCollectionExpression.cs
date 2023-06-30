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
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a MongoDB collection in the expression tree.
/// </summary>
internal class MongoCollectionExpression : Expression
{
    /// <summary>
    /// Create a <see cref="MongoCollectionExpression"/> for a given <see cref="IEntityType"/>.
    /// </summary>
    /// <param name="entityType"></param>
    public MongoCollectionExpression(IEntityType entityType)
    {
        EntityType = entityType;
        CollectionName = entityType.GetCollectionName();
    }

    /// <summary>
    /// Which entity this collection relates to.
    /// </summary>
    public IEntityType EntityType { get; }

    /// <summary>
    /// The name of this collection in MongoDB;
    /// </summary>
    public string CollectionName { get; }

    /// <inheritdoc/>
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc/>
    public override Type Type
        => EntityType.ClrType;

    /// <inheritdoc/>
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => this;

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(EntityType);

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is MongoCollectionExpression collectionExpression
               && EntityType.Equals(collectionExpression.EntityType));
}
