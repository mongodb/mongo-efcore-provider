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
using MongoDB.Bson;
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
    private readonly IEntityType _rootEntityType;
    private readonly ParameterExpression _docParameter;

    public MongoMixedProjectionBindingRemovingExpressionVisitor(
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        QueryTrackingBehavior trackingBehavior)
        : base(rootEntityType, queryExpression, docParameter, trackingBehavior)
    {
        _queryExpression = queryExpression;
        _rootEntityType = rootEntityType;
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

                // A scalar member access on a singleton (reference) navigation, e.g. select o.Customer.City.
                // The source expression is a MemberExpression whose source is the navigation's
                // StructuralTypeShaperExpression. The property belongs to the navigation target entity, not the
                // query root, so it must be read from the joined sub-document (the driver's native LeftJoin
                // places the lone joined reference under "_inner") rather than the root document.
                if (TryBindNavigationMemberAccess(sourceExpression, projectionBindingExpression.Type, out var navMemberRead))
                {
                    return navMemberRead;
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

                    // When using the driver's native Join, scalar properties read from the root entity
                    // live in the "_outer" sub-document, not at the document root. The resolver returns
                    // the root doc parameter for such accesses; redirect it to "_outer" here.
                    var docExpr = fieldAccess.DocumentExpression ?? _docParameter;
                    if (_queryExpression.UsesDriverJoinFields
                        && ReferenceEquals(docExpr, _docParameter))
                    {
                        docExpr = CreateGetValueExpression(_docParameter, "_outer", true, typeof(BsonDocument));
                    }

                    return CreateGetValueExpression(
                        docExpr,
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

    /// <summary>
    /// Binds a scalar member access on a singleton (reference) navigation in a mixed projection
    /// (e.g. <c>select new { A = o.Customer, B = o.Customer.City }</c>). The mapped expression is a
    /// <see cref="MemberExpression"/> (EF Core's <c>PropertyExpression</c>) whose source is the navigation
    /// target's <see cref="StructuralTypeShaperExpression"/>. Because the accessed property belongs to the
    /// navigation target rather than the query root, it is read from the joined sub-document: the driver's
    /// native LeftJoin places the lone joined reference under <c>"_inner"</c>. Returns <see langword="false"/>
    /// for anything that is not such a navigation member access so the caller can fall back to its other
    /// resolution paths.
    /// </summary>
    private bool TryBindNavigationMemberAccess(Expression? mappedExpression, Type resultType, out Expression result)
    {
        result = null!;

        if (mappedExpression is not MemberExpression memberExpression
            || memberExpression.Expression is not StructuralTypeShaperExpression shaper
            || shaper.StructuralType is not IEntityType targetEntityType)
        {
            return false;
        }

        // Only handle member access on a JOINED navigation target. A member access on the root entity's own
        // shaper (e.g. select new { o, o.CustomerID }) is a root-level property and is handled by the
        // existing TryResolveFieldAccess path, which reads it from "_outer". Reading it from "_inner" here
        // would return the wrong (joined) document's value.
        if (targetEntityType == _rootEntityType)
        {
            return false;
        }

        var property = targetEntityType.FindProperty(memberExpression.Member);
        if (property == null)
        {
            return false;
        }

        // Only the driver-native single-reference join shape (joined document under "_inner") is supported
        // here; other shapes fall through to the existing resolution paths / translation failure.
        if (!_queryExpression.UsesDriverJoinFields)
        {
            return false;
        }

        var innerDoc = CreateGetValueExpression(_docParameter, "_inner", false, typeof(BsonDocument));
        result = CreateGetValueExpression(innerDoc, property, resultType);
        return true;
    }
}
