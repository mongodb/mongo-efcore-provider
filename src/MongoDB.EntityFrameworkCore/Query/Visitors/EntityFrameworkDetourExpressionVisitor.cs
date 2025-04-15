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
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Adjust any expressions that EF might interfere with in undesirable ways in order to stop it.
/// </summary>
internal class EntityFrameworkDetourExpressionVisitor(QueryCompilationContext queryCompilationContext)
    : ExpressionVisitor
{
    protected override Expression VisitBinary(BinaryExpression b)
    {
        return b switch
        {
            {NodeType: not (ExpressionType.Equal or ExpressionType.NotEqual)}
                => base.VisitBinary(b),

            // Prevent EF changing (e.childCollection == null) to (e == null)
            {Right: ConstantExpression {Value: null}, Left: MemberExpression leftMember} when
                GetNavigationCollectionResultType(leftMember) is { } leftType
                => CreateCastedNullComparison(leftMember, leftType, b.NodeType),

            // Prevent EF changing (null != e.childCollection) to (null == e)
            {Right: MemberExpression rightMember, Left: ConstantExpression {Value: null}} when
                GetNavigationCollectionResultType(rightMember) is { } rightType
                => CreateCastedNullComparison(rightMember, rightType, b.NodeType),

            _ => base.VisitBinary(b)
        };
    }

    private Type? GetNavigationCollectionResultType(MemberExpression memberExpression)
    {
        var clrType = memberExpression.Member.ReflectedType;
        if (clrType == null) return null;

        var entityType = queryCompilationContext.Model.FindEntityType(clrType);
        var navigation = entityType?.FindNavigation(memberExpression.Member);
        if (navigation == null || navigation.IsCollection == false) return null;

        return navigation.PropertyInfo?.PropertyType ?? navigation.FieldInfo?.FieldType;
    }

    private static BinaryExpression CreateCastedNullComparison(MemberExpression memberExpression, Type toType,
        ExpressionType nodeType)
    {
        return nodeType == ExpressionType.Equal
            ? Expression.Equal(Expression.Convert(memberExpression, toType), Expression.Constant(null))
            : Expression.NotEqual(Expression.Convert(memberExpression, toType), Expression.Constant(null));
    }
}
