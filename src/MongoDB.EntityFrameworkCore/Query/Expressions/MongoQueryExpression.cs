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
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a top-level MongoDB-specific collection for querying server-side.
/// </summary>
internal sealed class MongoQueryExpression : Expression
{
    private IDictionary<ProjectionMember, Expression> _projectionMapping = new Dictionary<ProjectionMember, Expression>();
    private readonly List<ProjectionExpression> _projection = new();

    /// <summary>
    /// Create a <see cref="MongoQueryExpression"/> for the given entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> this collection relates to.</param>
    public MongoQueryExpression(IEntityType entityType)
    {
        CollectionExpression = new MongoCollectionExpression(entityType);
        _projectionMapping[new ProjectionMember()] =
            new EntityProjectionExpression(entityType, new RootReferenceExpression(entityType));
    }

    /// <summary>
    /// Represents the Mongo collection this query is bound to.
    /// </summary>
    public MongoCollectionExpression CollectionExpression { get; private set; }

    /// <summary>
    /// The <see cref="Expression"/> captured from the original EF-bound LINQ query.
    /// </summary>
    public Expression? CapturedExpression { get; set; }

    /// <inheritdoc />
    public override Type Type
        => typeof(object);

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public int AddToProjection(Expression expression, string? alias = null)
    {
        int existingIndex = _projection.FindIndex(pe => pe.Expression.Equals(expression));
        if (existingIndex != -1)
        {
            return existingIndex;
        }

        string? baseAlias = alias ?? (expression as IAccessExpression)?.Name;

        string? currentAlias = baseAlias;
        int counter = 0;
        while (_projection.Any(pe => string.Equals(pe.Alias, currentAlias, StringComparison.OrdinalIgnoreCase)))
        {
            currentAlias = $"{baseAlias}{counter++}";
        }

        _projection.Add(new ProjectionExpression(expression, currentAlias));

        return _projection.Count - 1;
    }

    public Expression GetMappedProjection(ProjectionMember projectionMember)
        => _projectionMapping[projectionMember];

    public IReadOnlyList<ProjectionExpression> Projection
        => _projection;

    public void ApplyProjection()
    {
        if (Projection.Any())
        {
            return;
        }

        var result = new Dictionary<ProjectionMember, Expression>();
        foreach (var (projectionMember, expression) in _projectionMapping)
        {
            result[projectionMember] = Constant(
                AddToProjection(
                    expression,
                    projectionMember.Last?.Name));
        }

        _projectionMapping = result;
    }

    public void ReplaceProjectionMapping(IDictionary<ProjectionMember, Expression> projectionMapping)
    {
        _projectionMapping.Clear();
        foreach (var (projectionMember, expression) in projectionMapping)
        {
            _projectionMapping[projectionMember] = expression;
        }
    }
}
