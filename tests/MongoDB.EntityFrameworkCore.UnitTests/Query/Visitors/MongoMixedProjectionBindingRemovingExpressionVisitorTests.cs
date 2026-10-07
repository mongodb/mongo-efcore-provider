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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Visitors;

// ReadDocumentConstructionMember: a join-scope-sourced member has a dotted ElementName relative to the outer
// document (e.g. "_lookup_Customer.CustomerID") and must be read via BsonBinding.CreateGetPropertyValueAtPath;
// an undotted root-relative member is read directly. The visitor is internal sealed, so the method is invoked
// via reflection (as in MongoProjectionBindingRemovingExpressionVisitorTests).
public class MongoMixedProjectionBindingRemovingExpressionVisitorTests
{
    private class Row
    {
        public string Id { get; set; } = null!;
        public string CustomerID { get; set; } = null!;
    }

    private static readonly MethodInfo ReadDocumentConstructionMemberMethodInfo
        = typeof(MongoMixedProjectionBindingRemovingExpressionVisitor)
            .GetMethod("ReadDocumentConstructionMember", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Expression CallReadDocumentConstructionMember(
        MongoMixedProjectionBindingRemovingExpressionVisitor visitor,
        MongoDocumentConstructionExpression construction, string alias, string memberName,
        MongoFieldExpression field, Type memberType)
    {
        try
        {
            return (Expression)ReadDocumentConstructionMemberMethodInfo.Invoke(
                visitor, [construction, alias, memberName, field, memberType])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    // The single-property reader always wraps in Expression.Convert; CreateGetPropertyValueAtPath doesn't.
    // Unwrap one Convert so assertions are about which reader ran.
    private static Expression Unwrap(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert } unary ? unary.Operand : expression;

    private static (MongoQueryExpression Query, IEntityType EntityType) TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Row>();
        var entityType = db.Model.FindEntityType(typeof(Row))!;
        return (new MongoQueryExpression(entityType), entityType);
    }

    private static MongoMixedProjectionBindingRemovingExpressionVisitor CreateVisitor(
        MongoQueryExpression queryExpression, IEntityType rootEntityType)
        => new(rootEntityType, queryExpression, Expression.Parameter(typeof(BsonDocument), "d"), QueryTrackingBehavior.NoTracking);

    [Fact]
    public void ReadDocumentConstructionMember_uses_dotted_path_reader_for_join_scope_sourced_field()
    {
        var (queryExpression, entityType) = TestQuery();
        var customerIdProperty = entityType.FindProperty(nameof(Row.CustomerID))!;

        var field = new MongoFieldExpression(customerIdProperty, "_lookup_Customer.CustomerID");
        var construction = new MongoDocumentConstructionExpression(
            Expression.New(typeof(object).GetConstructor(Type.EmptyTypes)!),
            [("Id", field)]);

        var visitor = CreateVisitor(queryExpression, entityType);

        var read = CallReadDocumentConstructionMember(visitor, construction, "CustomerId", "Id", field, typeof(string));

        var call = Assert.IsAssignableFrom<MethodCallExpression>(Unwrap(read));
        Assert.Equal("GetPropertyValueAtPath", call.Method.Name);
    }

    [Fact]
    public void ReadDocumentConstructionMember_keeps_single_property_reader_for_root_relative_field()
    {
        var (queryExpression, entityType) = TestQuery();
        var idProperty = entityType.FindProperty(nameof(Row.Id))!;

        var field = new MongoFieldExpression(idProperty, "Id"); // undotted
        var construction = new MongoDocumentConstructionExpression(
            Expression.New(typeof(object).GetConstructor(Type.EmptyTypes)!),
            [("Id", field)]);

        var visitor = CreateVisitor(queryExpression, entityType);

        var read = CallReadDocumentConstructionMember(visitor, construction, "Book", "Id", field, typeof(string));

        var call = Assert.IsAssignableFrom<MethodCallExpression>(Unwrap(read));
        Assert.NotEqual("GetPropertyValueAtPath", call.Method.Name);
    }
}
