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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Extends <see cref="MongoProjectionBindingRemovingExpressionVisitor"/> to handle mixed projections
/// (containing both entity references and scalar properties). In this path, the LINQ V3 query
/// returns full BsonDocuments (Select is stripped), and scalars are read from the root document
/// using the property's actual serialization info rather than the projection alias.
/// </summary>
internal sealed class MongoMixedProjectionBindingRemovingExpressionVisitor
    : MongoProjectionBindingRemovingExpressionVisitor
{
    private readonly MongoQueryExpression _queryExpression;
    private readonly IEntityType _rootEntityType;
    private readonly ParameterExpression _docParameter;

    public MongoMixedProjectionBindingRemovingExpressionVisitor(
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        bool trackQueryResults)
        : base(rootEntityType, queryExpression, docParameter, trackQueryResults)
    {
        _queryExpression = queryExpression;
        _rootEntityType = rootEntityType;
        _docParameter = docParameter;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        if (extensionExpression is ProjectionBindingExpression projectionBindingExpression)
        {
            // Scalar projections in the mixed path use ProjectionMember (not Index) because
            // ApplyProjection() early-returns when entity projections already populated _projection.
            if (projectionBindingExpression.ProjectionMember != null)
            {
                var mappedExpression = _queryExpression.GetMappedProjection(
                    projectionBindingExpression.ProjectionMember);

                // If ApplyProjection already converted this to an index, use the standard path.
                if (mappedExpression is ConstantExpression { Value: int })
                {
                    return base.VisitExtension(extensionExpression);
                }

                // Resolve the IProperty from the mapped expression and read from the full document.
                var (property, fieldName) = TryResolveFieldAccess(mappedExpression);
                if (property != null)
                {
                    return CreateGetValueExpression(_docParameter, property, projectionBindingExpression.Type);
                }

                if (fieldName != null)
                {
                    // Non-model field (e.g., __score from $addFields) — read directly by name.
                    return BsonBinding.CreateGetElementValue(_docParameter, fieldName, projectionBindingExpression.Type);
                }

                // Fallback: read by projection member name from the root document.
                return CreateGetValueExpression(
                    _docParameter,
                    projectionBindingExpression.ProjectionMember.Last?.Name,
                    !projectionBindingExpression.Type.IsNullableType(),
                    projectionBindingExpression.Type);
            }

            // Index-based bindings are used for entity projections (set by MongoProjectionBindingExpressionVisitor).
            // Delegate to the base visitor which handles entity materialization.
            return base.VisitExtension(extensionExpression);
        }

        return base.VisitExtension(extensionExpression);
    }

    /// <summary>
    /// Attempts to resolve an <see cref="IProperty"/> and/or field name from a projection expression that
    /// represents a scalar property access on the entity (e.g., <c>p.name</c>) or a non-model field
    /// access (e.g., <c>Mql.Field(e, "__score", null)</c>).
    /// </summary>
    private (IProperty? property, string? fieldName) TryResolveFieldAccess(Expression expression)
    {
        // Unwrap converts
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        if (expression is MemberExpression memberExpression)
        {
            var property = _rootEntityType.FindProperty(memberExpression.Member);
            return (property, property != null ? null : memberExpression.Member.Name);
        }

        if (expression is MethodCallExpression methodCall)
        {
            // Handle EF.Property<T>(entity, "propertyName") method calls
            if (methodCall.Method.IsEFPropertyMethod()
                && methodCall.Arguments[1] is ConstantExpression { Value: string propertyName })
            {
                var property = _rootEntityType.FindProperty(propertyName);
                return (property, property != null ? null : propertyName);
            }

            // Handle Mql.Field<TDoc, TField>(entity, "fieldName", serializer) calls
            if (methodCall.Method is { Name: "Field", DeclaringType.FullName: "MongoDB.Driver.Mql" }
                && methodCall.Arguments[1] is ConstantExpression { Value: string fieldName })
            {
                var property = _rootEntityType.FindProperty(fieldName);
                return (property, property != null ? null : fieldName);
            }
        }

        return (null, null);
    }
}
