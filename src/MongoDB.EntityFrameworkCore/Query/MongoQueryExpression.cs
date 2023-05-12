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

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Represents a top-level MongoDB-specific collection for querying server-side.
/// </summary>
public class MongoQueryExpression : Expression
{
    // TODO: Capture projections, orderings, limit, offset, distinct etc.

    /// <summary>
    /// Create a <see cref="MongoQueryExpression"/> for the given entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> this collection relates to.</param>
    public MongoQueryExpression(IEntityType entityType)
        : this(new MongoCollectionExpression(entityType), entityType.GetCollectionName()!)
    {
    }


    /// <summary>
    /// Creates a <see cref="MongoQueryExpression"/> for the given entity type.
    /// </summary>
    /// <param name="fromExpression">The expression this query selects against.</param>
    /// <param name="collection">The name of the collection on the MongoDB server.</param>
    public MongoQueryExpression(MongoCollectionExpression fromExpression, string collection)
    {
        FromExpression = fromExpression;
        Collection = collection;
    }

    public virtual MongoCollectionExpression FromExpression { get; private set; }
    public virtual Expression? Predicate { get; private set; }
    public virtual Expression? Limit { get; private set; }

    /// <summary>
    /// The underlying name of the collection for this query in MongoDB.
    /// </summary>
    public virtual string Collection { get; }

    /// <inheritdoc />
    public override Type Type => typeof(object);

    /// <inheritdoc />
    public sealed override ExpressionType NodeType => ExpressionType.Extension;

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var changed = false;

        var fromExpression = (MongoCollectionExpression)visitor.Visit(FromExpression);
        changed |= fromExpression != FromExpression;

        var predicate = visitor.Visit(Predicate);
        changed |= predicate != Predicate;

        var limit = visitor.Visit(Limit);
        changed |= limit != Limit;

        return !changed ? this : new MongoQueryExpression(fromExpression, Collection) {Predicate = predicate, Limit = limit};
    }

    public virtual void ApplyPredicate(Expression expression)
    {
        if (expression is ConstantExpression constantExpression && constantExpression.Value is bool boolValue
                                                                && boolValue)
        {
            return;
        }

        Predicate = Predicate == null
            ? expression
            : MakeBinary( // Consider balancing
                ExpressionType.AndAlso,
                Predicate,
                expression);
    }

    public virtual void ApplyLimit(Expression limit)
    {
        Limit = limit;
    }
}
