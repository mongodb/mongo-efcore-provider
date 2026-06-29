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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="MongoExpressionTranslator"/>, which translates EF predicate/key-selector
/// lambda bodies into dialect-agnostic <see cref="MongoExpression"/> trees.
/// </summary>
public class MongoExpressionTranslatorTests
{
    // --- Entity model used across tests ---

    private class Customer
    {
        public ObjectId Id { get; set; }
        public int Age { get; set; }
        public string Name { get; set; } = "";
        public bool Active { get; set; }
    }

    /// <summary>
    /// Returns the entity type for <typeparamref name="T"/> from a minimal in-memory model.
    /// </summary>
    private static IEntityType GetEntityType<T>() where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        // We need the model to stay alive for the test — grab the entity type from the model directly.
        return db.Model.FindEntityType(typeof(T))!;
    }

    /// <summary>
    /// Creates a <see cref="MongoExpressionTranslator"/> for the given entity type.
    /// </summary>
    private static MongoExpressionTranslator NewTranslator(IEntityType entityType)
        => new(entityType);

    /// <summary>
    /// Extracts the body of a predicate lambda as a raw <see cref="Expression"/>.
    /// </summary>
    private static Expression PredicateBody<T>(Expression<Func<T, bool>> predicate)
        => predicate.Body;

    // ------------------------------------------------------------------
    // Test 1: simple comparison → MongoBinaryExpression(GreaterThan, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void Translates_simple_comparison_to_field_op()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Age > 21);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, bin.Operator);
        var field = Assert.IsType<MongoFieldExpression>(bin.Left);
        Assert.Equal("Age", field.ElementName);
        var constant = Assert.IsType<MongoConstantExpression>(bin.Right);
        Assert.Equal(21, constant.Value);
    }

    // ------------------------------------------------------------------
    // Test 2: conjunction → top-level MongoBinaryExpression(AndAlso, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void Conjunction_maps_to_AndAlso()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Age > 21 && c.Age < 65);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.AndAlso, bin.Operator);
        Assert.IsType<MongoBinaryExpression>(bin.Left);
        Assert.IsType<MongoBinaryExpression>(bin.Right);
    }

    // ------------------------------------------------------------------
    // Test 3: method call → returns false, result null
    // ------------------------------------------------------------------

    [Fact]
    public void Unsupported_method_call_reports_not_translatable()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Name.StartsWith("A"));
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Test 4: query parameter → MongoParameterExpression (B2 invariant)
    // Constructs a parameterized body by hand, mimicking what EF emits for
    // a captured local in `var minAge = 21; ctx.Set<Customer>().Where(c => c.Age > minAge)`.
    // ------------------------------------------------------------------

    [Fact]
    public void Query_parameter_becomes_MongoParameterExpression_not_constant()
    {
        var entityType = GetEntityType<Customer>();

        // Build: c.Age > <query-parameter>  — shape differs by EF version.
        var cParam = Expression.Parameter(typeof(Customer), "c");
        var ageMember = Expression.MakeMemberAccess(cParam, typeof(Customer).GetProperty(nameof(Customer.Age))!);

#if EF8 || EF9
        // EF8/EF9: query parameters are plain ParameterExpressions whose names start with the EF prefix.
        const string paramName = QueryCompilationContext.QueryParameterPrefix + "minAge_0";
        Expression efParam = Expression.Parameter(typeof(int), paramName);
#else
        // EF10: query parameters are QueryParameterExpression nodes.
        const string paramName = "__minAge_0";
        Expression efParam = new Microsoft.EntityFrameworkCore.Query.QueryParameterExpression(paramName, typeof(int));
#endif
        var body = Expression.GreaterThan(ageMember, efParam);

        var translator = NewTranslator(entityType);
        var translated = translator.TryTranslate(body, out var result);

        // The body should translate successfully …
        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.GreaterThan, bin.Operator);
        Assert.IsType<MongoFieldExpression>(bin.Left);
        // … and the right side must be a MongoParameterExpression, not a MongoConstantExpression.
        var mongoParam = Assert.IsType<MongoParameterExpression>(bin.Right);
        Assert.Equal(paramName, mongoParam.Name);
    }

    // ------------------------------------------------------------------
    // Test 5: bare boolean field → MongoFieldExpression (bool)
    // ------------------------------------------------------------------

    [Fact]
    public void Bare_boolean_field_translates_to_field_expression()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Active);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var field = Assert.IsType<MongoFieldExpression>(result);
        Assert.Equal("Active", field.ElementName);
    }

    // ------------------------------------------------------------------
    // Test 6: negated boolean field → MongoUnaryExpression(Not, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void Negated_boolean_field_translates_to_Not_unary()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => !c.Active);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var unary = Assert.IsType<MongoUnaryExpression>(result);
        Assert.Equal(MongoUnaryOperator.Not, unary.Operator);
        var field = Assert.IsType<MongoFieldExpression>(unary.Operand);
        Assert.Equal("Active", field.ElementName);
    }

    // ------------------------------------------------------------------
    // Test 7: OrElse → MongoBinaryExpression(OrElse, ...)
    // ------------------------------------------------------------------

    [Fact]
    public void OrElse_maps_to_OrElse()
    {
        var entityType = GetEntityType<Customer>();
        var body = PredicateBody<Customer>(c => c.Age < 18 || c.Age > 65);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.True(translated);
        var bin = Assert.IsType<MongoBinaryExpression>(result);
        Assert.Equal(MongoBinaryOperator.OrElse, bin.Operator);
    }

    // ------------------------------------------------------------------
    // Test 8: composite-PK property → returns false, result null
    // A property that is part of a composite primary key is stored under
    // "_id.<element>", which the native translator cannot address. It must
    // fall back to driver-LINQ rather than emit a $match against the wrong
    // top-level field.
    // ------------------------------------------------------------------

    private class OrderLine
    {
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    [Fact]
    public void Composite_PK_property_access_reports_not_translatable()
    {
        // Build a model where (OrderId, ProductId) form the composite primary key.
        using var db = SingleEntityDbContext.Create<OrderLine>(mb =>
            mb.Entity<OrderLine>().HasKey(e => new { e.OrderId, e.ProductId }));
        var entityType = db.Model.FindEntityType(typeof(OrderLine))!;

        // A predicate over one of the composite-PK components should be rejected.
        var body = PredicateBody<OrderLine>(ol => ol.OrderId == 10248);
        var translator = NewTranslator(entityType);

        var translated = translator.TryTranslate(body, out var result);

        Assert.False(translated);
        Assert.Null(result);
    }
}
