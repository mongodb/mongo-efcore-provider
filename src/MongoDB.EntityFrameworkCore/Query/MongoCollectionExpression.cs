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

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Represents a MongoDB collection in the expression tree.
/// </summary>
public class MongoCollectionExpression : Expression
{
    /// <summary>
    /// Creates a <see cref="MongoCollectionExpression"/> for a given <see cref="IEntityType"/>.
    /// </summary>
    /// <param name="entityType"></param>
    public MongoCollectionExpression(IEntityType entityType)
    {
        EntityType = entityType;
    }

    /// <summary>
    /// Which entity this collection is for.
    /// </summary>
    public IEntityType EntityType { get; }

    /// <inheritdoc/>
    public sealed override ExpressionType NodeType => ExpressionType.Extension;

    /// <inheritdoc/>
    public override Type Type => EntityType.ClrType;

    /// <inheritdoc/>
    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(EntityType);

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is MongoCollectionExpression collectionExpression
               && Equals(collectionExpression));

    private bool Equals(MongoCollectionExpression collectionExpression)
        => EntityType.Equals(collectionExpression.EntityType);
}
