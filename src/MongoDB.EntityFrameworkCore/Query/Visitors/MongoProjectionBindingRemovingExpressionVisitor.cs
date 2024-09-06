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
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Translates an shaper expression tree to use <see cref="BsonDocument"/> and the right
/// methods to obtain data instead of the <see cref="ValueBuffer"/> EF provides.
/// </summary>
internal class MongoProjectionBindingRemovingExpressionVisitorBase : ProjectionBindingRemovingExpressionVisitorBase
{
    private readonly MongoQueryExpression _queryExpression;
    private readonly IEntityType _rootEntityType;

    /// <summary>
    /// Create a <see cref="MongoProjectionBindingRemovingExpressionVisitorBase"/>.
    /// </summary>
    /// <param name="queryExpression">The <see cref="MongoQueryExpression"/> this visitor should use.</param>
    /// <param name="docParameter">The parameter that will hold the <see cref="BsonDocument"/> input parameter to the shaper.</param>
    /// <param name="trackQueryResults">
    /// <see langword="true"/> if the results from this query are being tracked for changes,
    /// <see langword="false"/> if they are not.
    /// </param>
    public MongoProjectionBindingRemovingExpressionVisitorBase(
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        bool trackQueryResults)
        : base(docParameter, trackQueryResults)
    {
        _queryExpression = queryExpression;
        _rootEntityType = rootEntityType;
    }

    /// <summary>
    /// Obtain the <see cref="ProjectionExpression"/> that describes how this <see cref="ProjectionBindingExpression"/> should be mapped.
    /// </summary>
    /// <param name="projectionBindingExpression">The <see cref="ProjectionBindingExpression"/> to obtain the projection for.</param>
    /// <returns>The <see cref="ProjectionExpression"/> that describes how it should be mapped.</returns>
    protected override ProjectionExpression GetProjection(ProjectionBindingExpression projectionBindingExpression)
        => _queryExpression.Projection[GetProjectionIndex(projectionBindingExpression)];

    /// <inheritdoc />
    protected override Expression CreateGetValueExpression(
        Expression docExpression,
        string? elementName,
        bool required,
        Type type,
        ITypeBase? declaredType = null,
        CoreTypeMapping? typeMapping = null)
    {
        var entityType = declaredType ?? docExpression switch
        {
            RootReferenceExpression rootReferenceExpression => rootReferenceExpression.EntityType,
            ObjectAccessExpression docAccessExpression => docAccessExpression.Navigation.TargetEntityType,
            _ => _rootEntityType
        };

        var innerExpression = docExpression;
        if (ProjectionBindings.TryGetValue(docExpression, out var innerVariable))
        {
            innerExpression = innerVariable;
        }
        else
        {
            innerExpression = docExpression switch
            {
                RootReferenceExpression => CreateGetValueExpression(DocParameter, null, required, typeof(BsonDocument)),
                ObjectAccessExpression docAccessExpression => CreateGetValueExpression(docAccessExpression.AccessExpression,
                    docAccessExpression.Name, required, typeof(BsonDocument)),
                _ => innerExpression
            };
        }

        return BsonBinding.CreateGetValueExpression(innerExpression, elementName, required, typeMapping?.ClrType ?? type,
            entityType);
    }

    private int GetProjectionIndex(ProjectionBindingExpression projectionBindingExpression)
        => projectionBindingExpression.ProjectionMember != null
            ? _queryExpression.GetMappedProjection(projectionBindingExpression.ProjectionMember).GetConstantValue<int>()
            : projectionBindingExpression.Index
              ?? throw new InvalidOperationException("Internal error - projection mapping has neither member nor index.");
}
