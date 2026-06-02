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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Extends <see cref="MongoProjectionBindingRemovingExpressionVisitor"/> to handle mixed projections
/// (containing both entity references and scalar properties). In this path, the LINQ V3 query
/// returns full BsonDocuments (Select is stripped), and scalars are read from the document that
/// owns the mapped property using the property's actual serialization info.
/// </summary>
internal sealed class MongoMixedProjectionBindingRemovingExpressionVisitor
    : MongoProjectionBindingRemovingExpressionVisitor
{
    private readonly MongoQueryExpression _queryExpression;
    private readonly ParameterExpression _docParameter;

    public MongoMixedProjectionBindingRemovingExpressionVisitor(
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        QueryTrackingBehavior trackingBehavior)
        : base(rootEntityType, queryExpression, docParameter, trackingBehavior)
    {
        _queryExpression = queryExpression;
        _docParameter = docParameter;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        if (extensionExpression is ProjectionBindingExpression projectionBindingExpression)
        {
            if (projectionBindingExpression.ProjectionMember != null)
            {
                var mappedExpression = _queryExpression.GetMappedProjection(
                    projectionBindingExpression.ProjectionMember);

                // Resolve the source expression: after ApplyProjection it's wrapped as Constant(index),
                // so we unwrap to get the actual stored expression; otherwise use it directly.
                Expression? sourceExpression;
                string? alias;
                if (mappedExpression is ConstantExpression { Value: int })
                {
                    var projection = GetProjection(projectionBindingExpression);
                    alias = projection.Alias;
                    if (alias is null)
                        return _docParameter;
                    sourceExpression = projection.Expression;
                }
                else
                {
                    alias = projectionBindingExpression.ProjectionMember.Last?.Name;
                    sourceExpression = mappedExpression;
                }

                var fieldAccess = TryResolveFieldAccess(sourceExpression);
                if (fieldAccess.Property != null)
                {
                    if (fieldAccess.DocumentExpression is ParameterExpression parameterExpression
                        && fieldAccess.MemberInfo != null
                        && fieldAccess.MemberInfo.DeclaringType?.IsAssignableFrom(parameterExpression.Type) == true)
                    {
                        var memberAccess = Expression.MakeMemberAccess(parameterExpression, fieldAccess.MemberInfo);
                        return memberAccess.Type == projectionBindingExpression.Type
                            ? memberAccess
                            : Expression.Convert(memberAccess, projectionBindingExpression.Type);
                    }

                    return CreateGetValueExpression(
                        fieldAccess.DocumentExpression ?? _docParameter,
                        fieldAccess.Property,
                        projectionBindingExpression.Type);
                }

                if (fieldAccess.FieldName != null)
                {
                    return BsonBinding.CreateGetElementValue(
                        fieldAccess.DocumentExpression ?? _docParameter,
                        fieldAccess.FieldName,
                        projectionBindingExpression.Type);
                }

                return CreateGetValueExpression(
                    _docParameter,
                    alias,
                    !projectionBindingExpression.Type.IsNullableType(),
                    projectionBindingExpression.Type);
            }

            return base.VisitExtension(extensionExpression);
        }

        return base.VisitExtension(extensionExpression);
    }
}
