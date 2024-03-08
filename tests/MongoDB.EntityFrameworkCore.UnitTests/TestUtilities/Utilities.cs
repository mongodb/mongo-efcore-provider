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

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

internal static class Utilities
{
    public static string? GetCollectionName<TEntity>(this DbContext context) =>
        context.Model.FindEntityType(typeof(TEntity))?.GetCollectionName();

    public static IProperty? GetProperty<TEntity, TProperty>(this DbContext context,
        Expression<Func<TEntity, TProperty>> propertyExpression)
        => context.Model.FindEntityType(typeof(TEntity))?.FindProperty(propertyExpression.GetMemberAccess());

    public static PropertyInfo GetPropertyInfo<TEntity, TProperty>(Expression<Func<TEntity, TProperty>> propertyExpression)
    {
        return propertyExpression.Body.NodeType switch
        {
            ExpressionType.MemberAccess => (PropertyInfo)((MemberExpression)propertyExpression.Body).Member,
            _ => throw new NotSupportedException($"NodeType '{propertyExpression.Body.NodeType}' not supported.")
        };
    }
}
