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

// native-join-scope-nested-projection: MongoMixedProjectionBindingRemovingExpressionVisitor.
// ReadDocumentConstructionMember used to unconditionally read field.Property directly off the outer
// document, which is correct for the EF-447 root-relative shape but wrong for a join-scope-sourced
// member (Task 1 of this ticket), whose MongoFieldExpression.ElementName is a DOTTED path relative to
// the outer document (e.g. "_lookup_Customer.CustomerID"). These tests pin both: the new dotted-path
// read via BsonBinding.CreateGetPropertyValueAtPath, and the pre-existing undotted read unchanged.
//
// MongoMixedProjectionBindingRemovingExpressionVisitor is internal sealed, so (matching this project's
// existing pattern in MongoProjectionBindingRemovingExpressionVisitorTests of driving a protected/private
// member directly via reflection rather than inventing a test-only subclass) ReadDocumentConstructionMember
// is invoked here through reflection on a real instance.
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

    // CreateGetValueExpression(docExpr, IProperty, Type) (the single-property reader) always wraps its
    // result in an outer Expression.Convert regardless of whether the types already agree, whereas
    // BsonBinding.CreateGetPropertyValueAtPath (the multi-segment reader) returns its MethodCallExpression
    // unwrapped. Unwrap a single Convert layer before inspecting the method, so the assertion is about
    // WHICH reader ran, not an incidental wrapping difference between the two.
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
